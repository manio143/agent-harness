using System.Collections.Immutable;
using System.Diagnostics;

namespace Agent.Harness.Threads;

public sealed class ThreadManager
{
    public bool HasDeliverableEnqueueNow(string threadId)
    {
        if (ProjectStatus(threadId) != ThreadStatus.Idle)
            return false;

        var items = LoadPendingInbox(threadId);
        return items.Any(e => e.Delivery == "enqueue");
    }

    public bool HasImmediateOrDeliverableEnqueue(string threadId)
    {
        var items = LoadPendingInbox(threadId);
        if (items.IsDefaultOrEmpty) return false;

        if (items.Any(i => i.Delivery == "immediate"))
            return true;

        return HasDeliverableEnqueueNow(threadId);
    }

    private ThreadStatus ProjectStatus(string threadId)
    {
        var committed = _store.LoadCommittedEvents(_sessionId, threadId);
        return ThreadStatusProjector.ProjectStatus(committed);
    }

    private readonly string _sessionId;
    private readonly IThreadStore _store;
    public ThreadManager(string sessionId, IThreadStore store)
    {
        _sessionId = sessionId;
        _store = store;
        _store.CreateMainIfMissing(sessionId);
    }



    public ImmutableArray<ThreadInfo> List()
    {
        return _store.ListThreads(_sessionId)
            .Select(m => new ThreadInfo(m.ThreadId, m.ParentThreadId, ProjectStatus(m.ThreadId), m.Intent))
            .ToImmutableArray();
    }

    public string CreateChildThread(string parentThreadId)
    {
        var id = "thr_" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow.ToString("O");
        _store.CreateThread(_sessionId, new ThreadMetadata(
            ThreadId: id,
            ParentThreadId: parentThreadId,
            Intent: null,
            CreatedAtIso: now,
            UpdatedAtIso: now));
        return id;
    }


    public string ForkChildThread(string parentThreadId, SessionState parentState)
    {
        Debug.Assert(parentState.Buffer == TurnBuffer.Empty, "Fork requires empty buffer (no in-flight streaming deltas)");

        var childId = CreateChildThread(parentThreadId);

        // For now we do not persist cloned state; we will when we promote ThreadState to a first-class
        // persisted snapshot. At minimum, copy the committed events for readback.
        // Route through the sink pipeline to preserve the "commits flow via IEventSink" invariant.
        var sink = new ThreadEventSink(_sessionId, childId, _store);
        foreach (var evt in parentState.Committed)
            sink.OnCommittedAsync(evt, CancellationToken.None).GetAwaiter().GetResult();

        return childId;
    }



    public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId)
    {
        var evts = _store.LoadCommittedEvents(_sessionId, threadId);

        return evts.SelectMany(e => e switch
            {
                UserMessage u => new[] { new ThreadMessage("user", u.Text) },
                InterThreadMessage it => new[] { new ThreadMessage("inter_thread", it.Text) },
                AssistantMessage a => new[] { new ThreadMessage("assistant", a.Text) },
                _ => Array.Empty<ThreadMessage>(),
            })
            .ToImmutableArray();
    }

    public void ReportIntent(string threadId, string intent)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta is null)
            throw new InvalidOperationException($"unknown_thread:{threadId}");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var next = meta with { Intent = intent, UpdatedAtIso = now };
        _store.SaveThreadMetadata(_sessionId, next);
    }


    private ImmutableArray<ThreadInboxMessageEnqueued> LoadPendingInbox(string threadId)
    {
        var committed = _store.LoadCommittedEvents(_sessionId, threadId);

        var deliveredIds = committed
            .OfType<ThreadInboxMessageDequeued>()
            .Where(d => d.ThreadId == threadId)
            .Select(d => d.EnvelopeId)
            .ToHashSet(StringComparer.Ordinal);

        return committed
            .OfType<ThreadInboxMessageEnqueued>()
            .Where(e => e.ThreadId == threadId)
            .Where(e => !deliveredIds.Contains(e.EnvelopeId))
            .ToImmutableArray();
    }
}
