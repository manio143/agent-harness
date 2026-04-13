using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Llm;
using Agent.Harness.Persistence;
using Agent.Harness.TitleGeneration;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Acp;

/// <summary>
/// ACP session agent backed by the harness.
///
/// Server responsibility: host ACP + provide concrete <see cref="IChatClient"/>.
/// Harness responsibility: invoke LLM, normalize MEAI stream into observed events, run reducer/effects,
/// persist committed events, and publish committed-only ACP <c>session/update</c> notifications.
/// </summary>
public sealed class HarnessAcpSessionAgent : IAcpSessionAgent
{
    private readonly string _sessionId;
    private readonly MeaiIChatClient _chat;
    private readonly IAcpSessionEvents _events;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;
    private readonly ISessionStore _store;
    private readonly IEffectExecutor _effects;

    private SessionState _state;
    private readonly Dictionary<string, IAcpToolCall> _toolCalls = new();

    public HarnessAcpSessionAgent(
        string sessionId,
        MeaiIChatClient chat,
        IAcpSessionEvents events,
        CoreOptions coreOptions,
        AcpPublishOptions publishOptions,
        ISessionStore store,
        SessionState initialState,
        IEffectExecutor? effects = null)
    {
        _sessionId = sessionId;
        _chat = chat;
        _events = events;
        _coreOptions = coreOptions;
        _publishOptions = publishOptions;
        _store = store;
        _state = initialState;
        _effects = effects ?? NullEffectExecutor.Instance;
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Ensure no dangling tool calls from a previous turn.
        if (turn.ToolCalls.ActiveToolCallIds.Count > 0)
        {
            await turn.ToolCalls.CancelAllAsync(cancellationToken).ConfigureAwait(false);
            _toolCalls.Clear();
        }


        var titleGen = new SessionTitleGenerator(new MeaiTitleChatClientAdapter(_chat));
        // Mode A loop: we may need to re-prompt after tool completion.
        // MVP safety: cap the number of model invocations per ACP prompt.
        const int maxModelCalls = 5;

        var includeUserMessage = true;

        for (var callIndex = 0; callIndex < maxModelCalls; callIndex++)
        {
            async IAsyncEnumerable<ObservedChatEvent> ObservedForCall(PromptRequest req, bool includeUser)
            {
                if (includeUser)
                {
                    var userText = ExtractUserText(req);
                    yield return new ObservedUserMessage(userText);
                }

                var rendered = Core.RenderPrompt(_state);
                var meaiMessages = rendered
                    .Select(m => new MeaiChatMessage(m.Role switch
                    {
                        ChatRole.User => MeaiChatRole.User,
                        ChatRole.Assistant => MeaiChatRole.Assistant,
                        _ => MeaiChatRole.System,
                    }, m.Text))
                    .ToList();

                var updates = _chat.GetStreamingResponseAsync(meaiMessages, cancellationToken: cancellationToken);

                await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken))
                    yield return o;
            }

            var runner = new SessionRunner(_coreOptions, titleGen, _effects);

            var result = await runner.RunTurnAsync(_state, ObservedForCall(request, includeUserMessage), cancellationToken)
                .ConfigureAwait(false);

            includeUserMessage = false;
            _state = result.Next;

            var sawAssistantMessage = false;
            var sawToolTerminal = false;

            foreach (var committed in result.NewlyCommitted)
            {
                _store.AppendCommitted(_sessionId, committed);

            // Publish committed output derived ONLY from committed events.
            switch (committed)
            {
                case AssistantMessage a:
                    sawAssistantMessage = true;
                    if (!_coreOptions.CommitAssistantTextDeltas)
                    {
                        await _events.SendSessionUpdateAsync(new AgentMessageChunk
                        {
                            Content = new Agent.Acp.Schema.TextContent { Text = a.Text },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case AssistantTextDelta d:
                    await _events.SendSessionUpdateAsync(new AgentMessageChunk
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = d.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case ReasoningTextDelta r when _publishOptions.PublishReasoning:
                    await _events.SendSessionUpdateAsync(new AgentThoughtChunk
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = r.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case ToolCallRequested req:
                {
                    var call = GetOrStart(turn, req.ToolId, req.ToolName);
                    _toolCalls[req.ToolId] = call;
                    break;
                }

                case ToolCallInProgress ip:
                {
                    if (_toolCalls.TryGetValue(ip.ToolId, out var call))
                        await call.InProgressAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                case ToolCallUpdate u:
                {
                    if (_toolCalls.TryGetValue(u.ToolId, out var call))
                    {
                        var text = u.Content.ValueKind == JsonValueKind.String
                            ? u.Content.GetString() ?? string.Empty
                            : u.Content.GetRawText();

                        await call.AddContentAsync(new ToolCallContentContent
                        {
                            Content = new Agent.Acp.Schema.TextContent { Text = text },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }

                case ToolCallCompleted done:
                {
                    sawToolTerminal = true;
                    if (_toolCalls.TryGetValue(done.ToolId, out var call))
                    {
                        await call.CompletedAsync(cancellationToken).ConfigureAwait(false);
                        _toolCalls.Remove(done.ToolId);
                    }
                    break;
                }

                case ToolCallFailed failed:
                {
                    sawToolTerminal = true;
                    if (_toolCalls.TryGetValue(failed.ToolId, out var call))
                    {
                        await call.FailedAsync(failed.Error, cancellationToken).ConfigureAwait(false);
                        _toolCalls.Remove(failed.ToolId);
                    }
                    break;
                }

                case ToolCallCancelled cancelled:
                {
                    sawToolTerminal = true;
                    if (_toolCalls.TryGetValue(cancelled.ToolId, out var call))
                    {
                        await call.CancelledAsync(cancellationToken).ConfigureAwait(false);
                        _toolCalls.Remove(cancelled.ToolId);
                    }
                    break;
                }

                case ToolCallRejected rejected:
                {
                    sawToolTerminal = true;
                    var call = GetOrStart(turn, rejected.ToolId, title: "rejected");
                    var msg = rejected.Details.IsEmpty
                        ? rejected.Reason
                        : $"{rejected.Reason}: {string.Join(",", rejected.Details)}";

                    await call.FailedAsync(msg, cancellationToken).ConfigureAwait(false);
                    _toolCalls.Remove(rejected.ToolId);
                    break;
                }
            }
        }

            // Continue the Mode A loop if we executed at least one tool call and still haven't produced
            // an assistant message. (Typical pattern: model calls tool(s) first, then we re-prompt.)
            if (sawToolTerminal && !sawAssistantMessage)
                continue;

            break;
        }

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

    private static IAcpToolCall GetOrStart(IAcpPromptTurn turn, string toolId, string title)
    {
        // MVP: tool kind is not yet derived from schema; default to Read.
        return turn.ToolCalls.Start(toolId, title, new ToolKind(ToolKind.Read));
    }

    private static string ExtractUserText(PromptRequest request)
    {
        var first = request.Prompt.FirstOrDefault();
        if (first is Agent.Acp.Schema.TextContent t) return t.Text;
        return "";
    }
}
