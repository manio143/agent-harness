using System.Text.Json;
using Agent.Harness.Persistence;
using Microsoft.Extensions.AI;

using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Agent.Harness.TitleGeneration;

/// <summary>
/// Imperative-shell component that can generate and commit a session title as a committed event.
///
/// Metadata is a projection of committed events (e.g. JsonlSessionStore projects SessionTitleSet into session.json).
/// </summary>
public sealed class SessionTitleGenerator
{
    public const string SystemPrompt =
        "You're a title generator based on the following conversation <conversation>...</conversation> you must output precisely one short line that contains a title for this conversation.";

    private readonly IChatClient _chat;
    private readonly bool _logLlmPrompts;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;
    private readonly string? _sessionId;

    public SessionTitleGenerator(
        IChatClient chat,
        bool logLlmPrompts = false,
        Agent.Harness.Persistence.ISessionStore? store = null,
        string? sessionId = null)
    {
        _chat = chat;
        _logLlmPrompts = logLlmPrompts;
        _store = store;
        _sessionId = sessionId;
    }

    public async Task<SessionTitleSet?> MaybeGenerateAfterTurnAsync(SessionState state, CancellationToken cancellationToken)
    {
        if (state.Committed.Any(e => e is SessionTitleSet))
            return null;

        // Only generate after we have at least one assistant message committed.
        if (!state.Committed.Any(e => e is AssistantMessage))
            return null;

        var conversation = Core.RenderPrompt(state)
            .Select(m => $"{m.Role}: {m.Text}")
            .ToList();

        var user = "<conversation>\n" + string.Join("\n", conversation) + "\n</conversation>";

        var messages = new[]
        {
            new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.System, SystemPrompt),
            new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.User, user),
        };

        TryAppendPromptLog(messages);

        var resp = await _chat.GetResponseAsync(
            messages,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var titleRaw = resp.Text;
        if (string.IsNullOrWhiteSpace(titleRaw))
            return null;

        var line = titleRaw.Trim().Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Small clamp.
        if (line.Length > 80) line = line[..80];

        return new SessionTitleSet(line);
    }

    private void TryAppendPromptLog(IReadOnlyList<MeaiChatMessage> messages)
    {
        try
        {
            if (!_logLlmPrompts)
                return;

            if (_store is not JsonlSessionStore js)
                return;

            if (string.IsNullOrWhiteSpace(_sessionId))
                return;

            static object SerializeMessage(MeaiChatMessage m)
            {
                // Prefer lossless-ish logging: include text plus a best-effort summary of structured contents.
                var contents = m.Contents is null
                    ? Array.Empty<object>()
                    : m.Contents
                        .Select(c => (object)(c switch
                        {
                            MeaiTextContent tc => new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = tc.Text,
                            },
                            MeaiFunctionCallContent fc => new Dictionary<string, object?>
                            {
                                ["type"] = "function_call",
                                ["callId"] = fc.CallId,
                                ["name"] = fc.Name,
                                ["arguments"] = fc.Arguments,
                            },
                            MeaiFunctionResultContent fr => new Dictionary<string, object?>
                            {
                                ["type"] = "function_result",
                                ["callId"] = fr.CallId,
                                ["result"] = fr.Result,
                            },
                            _ => new Dictionary<string, object?>
                            {
                                ["type"] = c.GetType().Name,
                            },
                        }))
                        .ToArray();

                return new
                {
                    role = m.Role.ToString(),
                    text = m.Text,
                    contents,
                };
            }

            var promptPayload = new
            {
                purpose = "title_generation",
                messages = messages.Select(SerializeMessage),
                tools = Array.Empty<object>(),
            };

            var sessionDir = Path.Combine(js.RootDir, _sessionId!);
            Directory.CreateDirectory(sessionDir);

            var path = Path.Combine(sessionDir, "llm.prompt.jsonl");
            var line = JsonSerializer.Serialize(promptPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.AppendAllText(path, line + "\n");
        }
        catch
        {
            // best-effort logging only
        }
    }
}
