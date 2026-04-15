using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Harness.Acp;

/// <summary>
/// ACP adapter.
///
/// Current slice:
/// - Accepts an observed-event stream factory (standing in for MEAI streaming).
/// - Runs observed events through the core reducer via <see cref="TurnRunner"/>.
/// - Publishes ONLY committed events as ACP <c>session/update</c> notifications.
/// </summary>
public sealed record AcpPublishOptions(bool PublishReasoning = false);

public sealed class AcpSessionAgentAdapter : IAcpSessionAgent
{
    private readonly string _sessionId;
    private readonly IAcpSessionEvents _events;
    private readonly Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> _observed;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;

    private SessionState _state = SessionState.Empty;
    private readonly Dictionary<string, IAcpToolCall> _toolCalls = new();

    public AcpSessionAgentAdapter(
        string sessionId,
        IAcpSessionEvents events,
        Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> observed,
        CoreOptions? coreOptions = null,
        AcpPublishOptions? publishOptions = null)
    {
        _sessionId = sessionId;
        _events = events;
        _observed = observed;
        _coreOptions = coreOptions ?? new CoreOptions();
        _publishOptions = publishOptions ?? new AcpPublishOptions();
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Ensure no dangling tool calls from a previous turn.
        // Invariant: tool calls are scoped to a turn and should be closed deterministically.
        if (turn.ToolCalls.ActiveToolCallIds.Count > 0)
        {
            await turn.ToolCalls.CancelAllAsync(cancellationToken).ConfigureAwait(false);
            _toolCalls.Clear();
        }

        // Consume observed events and publish committed ones.
        await foreach (var committed in TurnRunner.RunAsync(
            _state,
            _observed(request),
            options: _coreOptions,
            onState: s => _state = s,
            cancellationToken: cancellationToken))
        {
            // Publish committed output derived ONLY from committed events.
            switch (committed)
            {
                case AssistantMessage a:
                    // In delta-commit mode, the UI has already received the full text via deltas.
                    // Publishing the full message again would typically duplicate content.
                    if (!_coreOptions.CommitAssistantTextDeltas)
                    {
                        await _events.SendSessionUpdateAsync(new AgentMessageChunk
                        {
                            Content = new TextContent { Text = a.Text },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case AssistantTextDelta d:
                    await _events.SendSessionUpdateAsync(new AgentMessageChunk
                    {
                        Content = new TextContent { Text = d.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case ReasoningMessage r when _publishOptions.PublishReasoning:
                    if (!_coreOptions.CommitReasoningTextDeltas)
                    {
                        await _events.SendSessionUpdateAsync(new AgentThoughtChunk
                        {
                            Content = new TextContent { Text = r.Text },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case ReasoningTextDelta r when _publishOptions.PublishReasoning:
                    await _events.SendSessionUpdateAsync(new AgentThoughtChunk
                    {
                        Content = new TextContent { Text = r.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case ToolCallRequested req:
                {
                    // Start tool call in ACP as soon as core commits the request.
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
                            Content = new TextContent { Text = text },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }

                case ToolCallCompleted done:
                {
                    if (_toolCalls.TryGetValue(done.ToolId, out var call))
                    {
                        await call.CompletedAsync(cancellationToken, rawOutput: done.Result).ConfigureAwait(false);
                        _toolCalls.Remove(done.ToolId);
                    }
                    break;
                }

                case ToolCallFailed failed:
                {
                    if (_toolCalls.TryGetValue(failed.ToolId, out var call))
                    {
                        await call.FailedAsync(failed.Error, cancellationToken).ConfigureAwait(false);
                        _toolCalls.Remove(failed.ToolId);
                    }
                    break;
                }

                case ToolCallCancelled cancelled:
                {
                    if (_toolCalls.TryGetValue(cancelled.ToolId, out var call))
                    {
                        await call.CancelledAsync(cancellationToken).ConfigureAwait(false);
                        _toolCalls.Remove(cancelled.ToolId);
                    }
                    break;
                }

                case ToolCallRejected rejected:
                {
                    // Represent rejection as a failed tool call (no execution).
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

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

    private static IAcpToolCall GetOrStart(IAcpPromptTurn turn, string toolId, string title)
    {
        // MVP: tool kind is not yet derived from schema; default to Read.
        return turn.ToolCalls.Start(toolId, title, ToolKindClassifier.ForToolName(title));
    }
}
