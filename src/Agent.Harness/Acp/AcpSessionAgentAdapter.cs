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
public sealed class AcpSessionAgentAdapter : IAcpSessionAgent
{
    private readonly string _sessionId;
    private readonly IAcpSessionEvents _events;
    private readonly Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> _observed;

    private SessionState _state = SessionState.Empty;

    public AcpSessionAgentAdapter(
        string sessionId,
        IAcpSessionEvents events,
        Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> observed)
    {
        _sessionId = sessionId;
        _events = events;
        _observed = observed;
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Consume observed events and publish committed ones.
        await foreach (var committed in TurnRunner.RunAsync(
            _state,
            _observed(request),
            options: null,
            onState: s => _state = s,
            cancellationToken: cancellationToken))
        {
            // Only assistant committed content is currently published.
            if (committed is AssistantMessageAdded a)
            {
                await _events.SendSessionUpdateAsync(new AgentMessageChunk
                {
                    Content = new TextContent { Text = a.Text },
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }
}
