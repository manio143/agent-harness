using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public sealed class InMemoryThreadStore : IThreadStore, IThreadCommittedEventAppender
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ThreadMetadata>> _meta = new();
    private readonly ConcurrentDictionary<(string sessionId, string threadId), List<SessionEvent>> _events = new();

    public void CreateMainIfMissing(string sessionId)
    {
        var session = _meta.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, ThreadMetadata>());
        session.TryAdd(ThreadIds.Main, new ThreadMetadata(
            ThreadId: ThreadIds.Main,
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Mode: ThreadMode.Multi,
            Model: null,
            CompactionCount: 0));
    }

    public ThreadMetadata? TryLoadThreadMetadata(string sessionId, string threadId)
    {
        if (!_meta.TryGetValue(sessionId, out var s)) return null;
        return s.TryGetValue(threadId, out var m) ? m : null;
    }

    public ImmutableArray<ThreadMetadata> ListThreads(string sessionId)
    {
        if (!_meta.TryGetValue(sessionId, out var s)) return ImmutableArray<ThreadMetadata>.Empty;
        return s.Values.OrderBy(v => v.CreatedAtIso).ToImmutableArray();
    }

    public void CreateThread(string sessionId, ThreadMetadata metadata)
    {
        var session = _meta.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, ThreadMetadata>());
        if (!session.TryAdd(metadata.ThreadId, metadata))
            throw new InvalidOperationException($"thread_already_exists:{metadata.ThreadId}");
    }

    public void SaveThreadMetadata(string sessionId, ThreadMetadata metadata)
    {
        var session = _meta.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, ThreadMetadata>());
        session[metadata.ThreadId] = metadata;
    }


    public void AppendCommittedEvent(string sessionId, string threadId, SessionEvent evt)
    {
        var list = _events.GetOrAdd((sessionId, threadId), _ => new List<SessionEvent>());
        lock (list) { list.Add(evt); }
    }

    public ImmutableArray<SessionEvent> LoadCommittedEvents(string sessionId, string threadId)
    {
        if (!_events.TryGetValue((sessionId, threadId), out var list)) return ImmutableArray<SessionEvent>.Empty;
        lock (list) { return list.ToImmutableArray(); }
    }
}
