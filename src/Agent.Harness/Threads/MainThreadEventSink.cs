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
    private readonly IThreadCommittedEventAppender _appender;
    private readonly Persistence.ISessionStore _sessionStore;
    private readonly bool _logObserved;

    public MainThreadEventSink(
        string sessionId,
        IThreadStore threadStore,
        IThreadCommittedEventAppender appender,
        Persistence.ISessionStore sessionStore,
        bool logObserved)
    {
        _sessionId = sessionId;
        _threadStore = threadStore;
        _appender = appender;
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
        _appender.AppendCommittedEvent(_sessionId, ThreadIds.Main, committed);

        // Best-effort thread metadata projection (main thread).
        switch (committed)
        {
            case SetModel set:
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                var meta = _threadStore.TryLoadThreadMetadata(_sessionId, ThreadIds.Main);
                var next = meta is null
                    ? new ThreadMetadata(
                        ThreadId: ThreadIds.Main,
                        ParentThreadId: null,
                        Intent: null,
                        CreatedAtIso: now,
                        UpdatedAtIso: now,
                        Mode: ThreadMode.Multi,
                        Model: set.Model,
                        CompactionCount: 0)
                    : meta with { Model = set.Model, UpdatedAtIso = now };

                _threadStore.SaveThreadMetadata(_sessionId, next);
                break;
            }

            case ThreadCompacted:
            {
                var meta = _threadStore.TryLoadThreadMetadata(_sessionId, ThreadIds.Main);
                if (meta is not null)
                {
                    var now = DateTimeOffset.UtcNow.ToString("O");
                    _threadStore.SaveThreadMetadata(_sessionId, meta with { CompactionCount = meta.CompactionCount + 1, UpdatedAtIso = now });
                }

                break;
            }
        }

        // Best-effort session metadata projection.
        var sessionMeta = _sessionStore.TryLoadMetadata(_sessionId);
        if (sessionMeta is not null)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            var projected = committed switch
            {
                SessionTitleSet t => sessionMeta with { Title = t.Title, UpdatedAtIso = now },
                _ => sessionMeta with { UpdatedAtIso = now },
            };

            _sessionStore.UpdateMetadata(_sessionId, projected);
        }

        return ValueTask.CompletedTask;
    }
}
