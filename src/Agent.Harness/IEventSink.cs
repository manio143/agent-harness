namespace Agent.Harness;

/// <summary>
/// Imperative-shell sink for session events.
///
/// - Observed events are non-publishable diagnostics.
/// - Committed events are durable and publishable.
///
/// Invariant: committed events MUST be sunk before executing any effects that were emitted as a result
/// of reducing the corresponding observed event.
/// </summary>
public interface IEventSink
{
    ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default);
    ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default);
}

public sealed class NullEventSink : IEventSink
{
    public static NullEventSink Instance { get; } = new();

    private NullEventSink() { }

    public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
