using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Persistence;
using Agent.Harness.Shell.Mcp;
using Microsoft.Extensions.AI;

using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Agent.Harness.Llm.CommandSuggestions;

public sealed partial class QuickWorkCommandIntentSuggester : ICommandIntentSuggester
{
    private const int MaxSuggestions = 8;

    private readonly IChatClient _chat;
    private readonly IPowerShellCommandCatalog _psCatalog;
    private readonly bool _logLlmPrompts;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;
    private readonly string? _sessionId;

    public QuickWorkCommandIntentSuggester(
        IChatClient chat,
        IPowerShellCommandCatalog psCatalog,
        bool logLlmPrompts = false,
        Agent.Harness.Persistence.ISessionStore? store = null,
        string? sessionId = null)
    {
        _chat = chat;
        _psCatalog = psCatalog;
        _logLlmPrompts = logLlmPrompts;
        _store = store;
        _sessionId = sessionId;
    }

    public async Task<ImmutableArray<CommandSuggestion>> SuggestAsync(
        string intent,
        ImmutableArray<ToolDefinition> offeredTools,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return ImmutableArray<CommandSuggestion>.Empty;

        var catalog = await BuildCommandCatalogAsync(intent, offeredTools, cancellationToken).ConfigureAwait(false);
        if (catalog is null)
            return ImmutableArray<CommandSuggestion>.Empty;

        // Split across multiple chat messages to reduce "instruction + data" confusion.
        // Goal: make the model treat the command list as data, and the JSON-only requirements as the last instruction.
        var messages = new[]
        {
            new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.System,
                "You are an AI assistant helping another agent. The agent provides an INTENT describing what it wants to do. " +
                "Your job is to select AT MOST 8 commands from the provided command list that best match the INTENT.\n\n" +
                "RESPONSE FORMAT\n" +
                "Return a JSON array of objects: { name: string, reason: string }[]\n\n" +
                "EXAMPLES\n" +
                "1) Intent: \"Find files relevant to authentication\"\n" +
                "   Response: [{\"name\":\"Get-ChildItem\",\"reason\":\"List files by name\"},{\"name\":\"Select-String\",\"reason\":\"Search text file content\"}]\n" +
                "2) Intent: \"Process CSV file\"\n" +
                "   Response: [{\"name\":\"Import-Csv\",\"reason\":\"Load CSV data into objects\"},{\"name\":\"Export-Csv\",\"reason\":\"Write processed objects back to CSV\"}]"),

            // Provide commands as data; keep it concise (name + description only).
            new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.System,
                "<Commands>\n" + catalog + "</Commands>"),

            // Provide intent as user input.
            new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.User, $"<intent>{intent}</intent>"),

            // Final instruction: JSON only.
            new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.System,
                "Please provide the suggested commands (max 8) for the intent. Reply with JSON only in the required format. " +
                "Response must start with '[' and end with ']'. Do not include any prose, markdown, or code fences."),
        };

        TryAppendPromptLog(messages);

        var resp = await _chat.GetResponseAsync(
            messages,
            options: new ChatOptions { Temperature = 0 },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = string.Join("\n", resp.Messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text));

        TryAppendResponseLog(resp);

        if (string.IsNullOrWhiteSpace(text))
            return ImmutableArray<CommandSuggestion>.Empty;

        if (TryParseSuggestions(text, out var parsed))
            return parsed;

        // Deterministic behavior: if the model didn't return valid STRICT JSON, return empty.
        return ImmutableArray<CommandSuggestion>.Empty;
    }

    private static bool TryParseSuggestions(string text, out ImmutableArray<CommandSuggestion> suggestions)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                suggestions = ImmutableArray<CommandSuggestion>.Empty;
                return false;
            }

            var b = ImmutableArray.CreateBuilder<CommandSuggestion>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!el.TryGetProperty("reason", out var r) || r.ValueKind != JsonValueKind.String) continue;

                var name = n.GetString() ?? "";
                var reason = r.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                b.Add(new CommandSuggestion(name.Trim(), reason.Trim()));
                if (b.Count >= MaxSuggestions) break;
            }

            suggestions = b.ToImmutable();
            return suggestions.Length > 0;
        }
        catch
        {
            suggestions = ImmutableArray<CommandSuggestion>.Empty;
            return false;
        }
    }


    private void TryAppendPromptLog(IReadOnlyList<MeaiChatMessage> messages)
    {
        TryAppendLlmJsonLine(new
        {
            purpose = "command_suggestions",
            messages = messages.Select(SerializeMeaiMessage),
            tools = Array.Empty<object>(),
        });
    }

    private void TryAppendResponseLog(ChatResponse resp)
    {
        // Log the raw model response for debugging (often contains invalid JSON or unexpected text).
        var rawText = string.Join("\n", resp.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(t => t.Text));

        TryAppendLlmJsonLine(new
        {
            purpose = "command_suggestions_response",
            rawText,
            messages = resp.Messages.Select(SerializeMeaiMessage),
            tools = Array.Empty<object>(),
        });
    }

    private void TryAppendLlmJsonLine(object payload)
    {
        try
        {
            if (!_logLlmPrompts)
                return;

            if (_store is not JsonlSessionStore js)
                return;

            if (string.IsNullOrWhiteSpace(_sessionId))
                return;

            var sessionDir = Path.Combine(js.RootDir, _sessionId!);
            Directory.CreateDirectory(sessionDir);

            var path = Path.Combine(sessionDir, "llm.prompt.jsonl");
            var line = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.AppendAllText(path, line + "\n");
        }
        catch
        {
            // best-effort logging only
        }
    }

    private static object SerializeMeaiMessage(MeaiChatMessage m)
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

    private static IEnumerable<(string Name, string Synopsis)> BuildMcpCmdletInfos(ImmutableArray<ToolDefinition> offeredTools)
    {
        var verbs = McpApprovedVerbs.CreateDefault();

        foreach (var t in offeredTools)
        {
            var idx = t.Name.IndexOf("__", StringComparison.Ordinal);
            if (idx <= 0 || idx >= t.Name.Length - 2) continue;

            var tool = t.Name[(idx + 2)..];
            var mapping = McpCmdletNameMapper.Map(tool, verbs);
            yield return (mapping.CmdletName, t.Description ?? "");
        }
    }
}
