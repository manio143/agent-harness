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
        }

        var publisher = new AcpCommittedEventPublisher(_events, _coreOptions, _publishOptions);
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        // Consume observed events and publish committed ones.
        await foreach (var committed in TurnRunner.RunWithEffectsAsync(
            _state,
            _observed(request),
            effects: NullEffectExecutor.Instance,
            options: _coreOptions,
            onState: s => _state = s,
            cancellationToken: cancellationToken))
        {
            await publisher.PublishAsync(committed, turn, toolCalls, cancellationToken).ConfigureAwait(false);
        }

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

}
