using System.Collections.Immutable;
using System.Diagnostics;

namespace Agent.Harness.Threads;

public sealed class ThreadManager
{
    public bool HasDeliverableEnqueueNow(string threadId)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta?.Status != ThreadStatus.Idle)
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

    private readonly string _sessionId;
    private readonly IThreadStore _store;
    public ThreadManager(string sessionId, IThreadStore store)
    {
        _sessionId = sessionId;
        _store = store;
        _store.CreateMainIfMissing(sessionId);
    }

    public void MarkRunning(string threadId)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta is null) return;
        _store.SaveThreadMetadata(_sessionId, meta with { Status = ThreadStatus.Running, UpdatedAtIso = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void MarkIdle(string threadId)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta is null) return;
        _store.SaveThreadMetadata(_sessionId, meta with { Status = ThreadStatus.Idle, UpdatedAtIso = DateTimeOffset.UtcNow.ToString("O") });
    }


    public ImmutableArray<ThreadInfo> List()
    {
        return _store.ListThreads(_sessionId)
            .Select(m => new ThreadInfo(m.ThreadId, m.ParentThreadId, m.Status, m.Intent))
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
            UpdatedAtIso: now,
            Status: ThreadStatus.Idle));
        return id;
    }


    public string ForkChildThread(string parentThreadId, SessionState parentState)
    {
        Debug.Assert(parentState.Buffer == TurnBuffer.Empty, "Fork requires empty buffer (no in-flight streaming deltas)");

        var childId = CreateChildThread(parentThreadId);

        // For now we do not persist cloned state; we will when we promote ThreadState to a first-class
        // persisted snapshot. At minimum, copy the committed events for readback.
        foreach (var evt in parentState.Committed)
            _store.AppendCommittedEvent(_sessionId, childId, evt);

        return childId;
    }



    public ImmutableArray<ThreadMessage> ReadAssistantMessages(string threadId)
    {
        var evts = _store.LoadCommittedEvents(_sessionId, threadId);
        return evts.OfType<AssistantMessage>()
            .Select(a => new ThreadMessage("assistant", a.Text))
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

        _store.AppendCommittedEvent(_sessionId, threadId, new ThreadIntentReported(intent));
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
