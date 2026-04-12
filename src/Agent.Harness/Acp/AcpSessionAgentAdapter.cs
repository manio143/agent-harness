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
        // Consume observed events and publish committed ones.
        await foreach (var committed in TurnRunner.RunAsync(
            _state,
            _observed(request),
            options: _coreOptions,
            onState: s => _state = s,
            cancellationToken: cancellationToken))
        {
            // Publish committed assistant output.
            switch (committed)
            {
                case AssistantMessageAdded a:
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

                case AssistantMessageDeltaAdded d:
                    await _events.SendSessionUpdateAsync(new AgentMessageChunk
                    {
                        Content = new TextContent { Text = d.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case ReasoningDeltaAdded r when _publishOptions.PublishReasoning:
                    await _events.SendSessionUpdateAsync(new AgentThoughtChunk
                    {
                        Content = new TextContent { Text = r.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }
}
