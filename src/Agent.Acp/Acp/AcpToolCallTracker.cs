using System.Collections.Concurrent;
using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

internal sealed class AcpPromptTurn : IAcpPromptTurn
{
    public AcpPromptTurn(IAcpToolCalls toolCalls)
    {
        ToolCalls = toolCalls;
    }

    public IAcpToolCalls ToolCalls { get; }
}

internal sealed class AcpToolCallTracker : IAcpToolCalls
{
    private enum State
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Cancelled,
    }

    private readonly IAcpSessionEvents _events;
    private readonly ConcurrentDictionary<string, State> _state = new();

    public AcpToolCallTracker(IAcpSessionEvents events)
    {
        _events = events;
    }

    public IReadOnlyCollection<string> ActiveToolCallIds => _state.Where(kvp => kvp.Value is State.Pending or State.InProgress).Select(kvp => kvp.Key).ToArray();

    public IAcpToolCall Start(string toolCallId, string title, ToolKind kind)
    {
        if (!_state.TryAdd(toolCallId, State.Pending))
            throw new InvalidOperationException($"ToolCall already exists: {toolCallId}");

        // Emit tool_call (pending)
        _ = _events.SendSessionUpdateAsync(new
        {
            sessionUpdate = "tool_call",
            toolCallId,
            title,
            kind,
            status = ToolCallStatus.Pending,
        });

        return new Handle(this, toolCallId);
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in ActiveToolCallIds)
        {
            var h = new Handle(this, id);
            await h.CancelledAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Handle : IAcpToolCall
    {
        private readonly AcpToolCallTracker _tracker;
        public Handle(AcpToolCallTracker tracker, string id)
        {
            _tracker = tracker;
            ToolCallId = id;
        }

        public string ToolCallId { get; }

        public Task InProgressAsync(CancellationToken cancellationToken = default) =>
            _tracker.TransitionAsync(ToolCallId, to: State.InProgress, message: null, content: null, cancellationToken);

        public Task CompletedAsync(IReadOnlyList<ToolCallContent> content, CancellationToken cancellationToken = default) =>
            _tracker.TransitionAsync(ToolCallId, to: State.Completed, message: null, content: content, cancellationToken);

        public Task FailedAsync(string message, IReadOnlyList<ToolCallContent>? content = null, CancellationToken cancellationToken = default) =>
            _tracker.TransitionAsync(ToolCallId, to: State.Failed, message: message, content: content, cancellationToken);

        public Task CancelledAsync(CancellationToken cancellationToken = default) =>
            _tracker.TransitionAsync(ToolCallId, to: State.Cancelled, message: "cancelled", content: null, cancellationToken);
    }

    private async Task TransitionAsync(string toolCallId, State to, string? message, IReadOnlyList<ToolCallContent>? content, CancellationToken cancellationToken)
    {
        if (!_state.TryGetValue(toolCallId, out var from))
            throw new InvalidOperationException($"Unknown toolCallId: {toolCallId}");

        var ok = (from, to) switch
        {
            (State.Pending, State.InProgress) => true,
            (State.Pending, State.Completed) => true,
            (State.Pending, State.Failed) => true,
            (State.Pending, State.Cancelled) => true,

            (State.InProgress, State.Completed) => true,
            (State.InProgress, State.Failed) => true,
            (State.InProgress, State.Cancelled) => true,

            _ => false,
        };

        if (!ok)
            throw new InvalidOperationException($"Invalid tool call transition {from} -> {to} for {toolCallId}");

        _state[toolCallId] = to;

        // Emit tool_call_update
        var status = to switch
        {
            State.InProgress => ToolCallStatus.InProgress,
            State.Completed => ToolCallStatus.Completed,
            State.Failed => ToolCallStatus.Failed,
            // ACP schema models cancellation as a prompt-turn StopReason, not a ToolCallStatus.
            // We map cancellation to failed for now (clients can infer cancellation from the turn-level stopReason).
            State.Cancelled => ToolCallStatus.Failed,
            _ => ToolCallStatus.Pending,
        };

        await _events.SendSessionUpdateAsync(new
        {
            sessionUpdate = "tool_call_update",
            toolCallId,
            status,
            content,
            rawOutput = message is null ? null : new { error = message },
        }, cancellationToken).ConfigureAwait(false);
    }
}
