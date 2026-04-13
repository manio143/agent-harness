using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Persistence;
using Agent.Harness.TitleGeneration;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;

namespace Agent.Harness.Acp;

/// <summary>
/// ACP session agent backed by the harness.
///
/// Responsibility split:
/// - Server: hosts ACP transport + wires concrete <see cref="MeaiIChatClient"/> implementation.
/// - Harness: owns turn orchestration (observed -> reduce -> effects -> observed), including MEAI model calls.
///
/// Invariant: ACP publishes committed-only events.
/// </summary>
public sealed class HarnessAcpSessionAgent : IAcpSessionAgent
{
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;
    private readonly IAcpSessionEvents _events;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;
    private readonly ISessionStore _store;
    private readonly IMcpToolInvoker _mcp;

    private SessionState _state;
    private readonly Dictionary<string, IAcpToolCall> _toolCalls = new();

    public HarnessAcpSessionAgent(
        string sessionId,
        IAcpClientCaller client,
        MeaiIChatClient chat,
        IAcpSessionEvents events,
        CoreOptions coreOptions,
        AcpPublishOptions publishOptions,
        ISessionStore store,
        SessionState initialState,
        IMcpToolInvoker? mcp = null)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _events = events;
        _coreOptions = coreOptions;
        _publishOptions = publishOptions;
        _store = store;
        _state = initialState;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Ensure no dangling tool calls from a previous turn.
        if (turn.ToolCalls.ActiveToolCallIds.Count > 0)
        {
            await turn.ToolCalls.CancelAllAsync(cancellationToken).ConfigureAwait(false);
            _toolCalls.Clear();
        }

        // Capability-gated tool catalog: only expose built-in tools that are runnable with the negotiated client capabilities.
        // If tools were already populated (e.g. session/load replay), keep them.
        if (_state.Tools.IsDefaultOrEmpty)
        {
            _state = _state with { Tools = ClientToolCatalog.BuildBuiltins(_client.ClientCapabilities) };
        }

        async IAsyncEnumerable<ObservedChatEvent> ObservedUserInput()
        {
            var userText = ExtractUserText(request);
            yield return new ObservedUserMessage(userText);
        }

        var titleGen = new SessionTitleGenerator(new Llm.MeaiTitleChatClientAdapter(_chat));
        var effects = new AcpEffectExecutor(_sessionId, _client, _chat, _mcp);
        var runner = new SessionRunner(_coreOptions, titleGen, effects);

        var result = await runner.RunTurnAsync(_state, ObservedUserInput(), cancellationToken).ConfigureAwait(false);
        _state = result.Next;

        foreach (var committed in result.NewlyCommitted)
        {
            _store.AppendCommitted(_sessionId, committed);
            await PublishCommittedAsync(committed, turn, cancellationToken).ConfigureAwait(false);
        }

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

    private async Task PublishCommittedAsync(SessionEvent committed, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Publish committed output derived ONLY from committed events.
        switch (committed)
        {
            case AssistantMessage a:
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
                _toolCalls[req.ToolId] = GetOrStart(turn, req.ToolId, title: req.ToolName);
                break;

            case ToolCallInProgress ip:
                if (_toolCalls.TryGetValue(ip.ToolId, out var inProgress))
                    await inProgress.InProgressAsync(cancellationToken).ConfigureAwait(false);
                break;

            case ToolCallUpdate u:
                if (_toolCalls.TryGetValue(u.ToolId, out var updateCall))
                {
                    var text = u.Content.ValueKind == JsonValueKind.String
                        ? u.Content.GetString() ?? string.Empty
                        : u.Content.GetRawText();

                    await updateCall.AddContentAsync(new ToolCallContentContent
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = text },
                    }, cancellationToken).ConfigureAwait(false);
                }
                break;

            case ToolCallCompleted done:
                if (_toolCalls.TryGetValue(done.ToolId, out var completed))
                {
                    await completed.CompletedAsync(cancellationToken).ConfigureAwait(false);
                    _toolCalls.Remove(done.ToolId);
                }
                break;

            case ToolCallFailed failed:
                if (_toolCalls.TryGetValue(failed.ToolId, out var failedCall))
                {
                    await failedCall.FailedAsync(failed.Error, cancellationToken).ConfigureAwait(false);
                    _toolCalls.Remove(failed.ToolId);
                }
                break;

            case ToolCallCancelled cancelled:
                if (_toolCalls.TryGetValue(cancelled.ToolId, out var cancelledCall))
                {
                    await cancelledCall.CancelledAsync(cancellationToken).ConfigureAwait(false);
                    _toolCalls.Remove(cancelled.ToolId);
                }
                break;

            case ToolCallRejected rejected:
            {
                var call = GetOrStart(turn, rejected.ToolId, title: "rejected");
                var msg = rejected.Details.IsEmpty
                    ? rejected.Reason
                    : $"{rejected.Reason}: {string.Join(",", rejected.Details)}";

                await call.FailedAsync(msg, cancellationToken).ConfigureAwait(false);
                _toolCalls.Remove(rejected.ToolId);
                break;
            }

            // Turn markers are currently not published to ACP.
            case TurnStarted:
            case TurnEnded:
            case UserMessage:
            case SessionTitleSet:
            case ModelInvoked:
            case ToolCallPermissionApproved:
            case ToolCallPermissionDenied:
            case ToolCallPending:
                break;
        }
    }

    private static IAcpToolCall GetOrStart(IAcpPromptTurn turn, string toolId, string title)
    {
        return turn.ToolCalls.Start(toolId, title, new ToolKind(ToolKind.Read));
    }

    private static string ExtractUserText(PromptRequest request)
    {
        var first = request.Prompt.FirstOrDefault();
        if (first is Agent.Acp.Schema.TextContent t) return t.Text;
        return "";
    }
}
