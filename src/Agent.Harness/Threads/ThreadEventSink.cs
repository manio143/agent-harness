using System.Collections.Immutable;

namespace Agent.Harness.Threads;

/// <summary>
/// Persists committed session events into a specific thread's event log.
/// Does not project to ACP.
/// </summary>
public sealed class ThreadEventSink : IEventSink
{
    private readonly string _sessionId;
    private readonly string _threadId;
    private readonly IThreadStore _store;

    public ThreadEventSink(string sessionId, string threadId, IThreadStore store)
    {
        _sessionId = sessionId;
        _threadId = threadId;
        _store = store;
    }

    public ValueTask OnObservedAsync(ObservedChatEvent evt, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnCommittedAsync(SessionEvent evt, CancellationToken cancellationToken)
    {
        _store.AppendCommittedEvent(_sessionId, _threadId, evt);
        return ValueTask.CompletedTask;
    }
}
