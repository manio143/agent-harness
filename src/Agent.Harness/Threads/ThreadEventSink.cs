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
    private readonly IThreadCommittedEventAppender _appender;

    public ThreadEventSink(string sessionId, string threadId, IThreadStore store, IThreadCommittedEventAppender appender)
    {
        _sessionId = sessionId;
        _threadId = threadId;
        _store = store;
        _appender = appender;
    }

    public ValueTask OnObservedAsync(ObservedChatEvent evt, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnCommittedAsync(SessionEvent evt, CancellationToken cancellationToken)
    {
        _appender.AppendCommittedEvent(_sessionId, _threadId, evt);

        // Projection: keep thread metadata in sync with important committed events.
        var meta = _store.TryLoadThreadMetadata(_sessionId, _threadId);
        if (meta is null)
            return ValueTask.CompletedTask;

        var now = DateTimeOffset.UtcNow.ToString("O");

        switch (evt)
        {
            case SetModel setModel:
                _store.SaveThreadMetadata(_sessionId, meta with { Model = setModel.Model, UpdatedAtIso = now });
                break;

            case ThreadCompacted:
                _store.SaveThreadMetadata(_sessionId, meta with { CompactionCount = meta.CompactionCount + 1, UpdatedAtIso = now });
                break;
        }

        return ValueTask.CompletedTask;
    }
}
