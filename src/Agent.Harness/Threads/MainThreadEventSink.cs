namespace Agent.Harness.Threads;

/// <summary>
/// Persists committed events for the main thread into the thread store,
/// while still projecting metadata updates (updatedAt/title) into the session metadata file.
///
/// Design grounding:
/// - Main thread is just another thread (same committed event store mechanism).
/// - Session metadata remains a session-level artifact (session.json) and may be updated
///   as a projection of committed events.
/// </summary>
public sealed class MainThreadEventSink : IEventSink
{
    private readonly string _sessionId;
    private readonly IThreadStore _threadStore;
    private readonly Persistence.ISessionStore _sessionStore;
    private readonly bool _logObserved;

    public MainThreadEventSink(string sessionId, IThreadStore threadStore, Persistence.ISessionStore sessionStore, bool logObserved)
    {
        _sessionId = sessionId;
        _threadStore = threadStore;
        _sessionStore = sessionStore;
        _logObserved = logObserved;
    }

    public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default)
    {
        if (!_logObserved)
            return ValueTask.CompletedTask;

        if (_sessionStore is not Persistence.JsonlSessionStore jsonl)
            return ValueTask.CompletedTask;

        var path = Path.Combine(jsonl.RootDir, _sessionId, "observed.jsonl");
        File.AppendAllText(path, ObservedEventJson.ToJsonl(observed) + "\n");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default)
    {
        _threadStore.AppendCommittedEvent(_sessionId, ThreadIds.Main, committed);

        // Best-effort session metadata projection.
        var meta = _sessionStore.TryLoadMetadata(_sessionId);
        if (meta is not null)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            var projected = committed switch
            {
                SessionTitleSet t => meta with { Title = t.Title, UpdatedAtIso = now },
                _ => meta with { UpdatedAtIso = now },
            };

            _sessionStore.UpdateMetadata(_sessionId, projected);
        }

        return ValueTask.CompletedTask;
    }
}
