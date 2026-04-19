using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Harness.Acp;

/// <summary>
/// Publishes committed harness events to ACP (session/update + tool call lifecycle).
///
/// This is a pure-ish boundary component: it does not run the reducer.
/// It just maps committed <see cref="SessionEvent"/>s to ACP notifications/tool call updates.
/// </summary>
public sealed class AcpCommittedEventPublisher
{
    private readonly IAcpSessionEvents _events;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;

    public AcpCommittedEventPublisher(
        IAcpSessionEvents events,
        CoreOptions coreOptions,
        AcpPublishOptions publishOptions)
    {
        _events = events;
        _coreOptions = coreOptions;
        _publishOptions = publishOptions;
    }

    public async Task PublishAsync(
        SessionEvent committed,
        IAcpPromptTurn turn,
        Dictionary<string, IAcpToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
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
                toolCalls[req.ToolId] = call;
                break;
            }

            case ToolCallInProgress ip:
            {
                if (toolCalls.TryGetValue(ip.ToolId, out var call))
                    await call.InProgressAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            case ToolCallUpdate u:
            {
                if (toolCalls.TryGetValue(u.ToolId, out var call))
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
                if (toolCalls.TryGetValue(done.ToolId, out var call))
                {
                    await call.CompletedAsync(cancellationToken, rawOutput: done.Result).ConfigureAwait(false);
                    toolCalls.Remove(done.ToolId);
                }
                break;
            }

            case ToolCallFailed failed:
            {
                if (toolCalls.TryGetValue(failed.ToolId, out var call))
                {
                    await call.FailedAsync(failed.Error, cancellationToken).ConfigureAwait(false);
                    toolCalls.Remove(failed.ToolId);
                }
                break;
            }

            case ToolCallCancelled cancelled:
            {
                if (toolCalls.TryGetValue(cancelled.ToolId, out var call))
                {
                    await call.CancelledAsync(cancellationToken).ConfigureAwait(false);
                    toolCalls.Remove(cancelled.ToolId);
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
                toolCalls.Remove(rejected.ToolId);
                break;
            }
        }
    }

    private static IAcpToolCall GetOrStart(IAcpPromptTurn turn, string toolId, string title)
    {
        // MVP: tool kind is not yet derived from schema; default to Read.
        return turn.ToolCalls.Start(toolId, title, ToolKindClassifier.ForToolName(title));
    }
}
