using System.Collections.Immutable;
using System.Diagnostics;

namespace Agent.Harness.Threads;

public sealed class ThreadManager
{
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
            .Select(m => new ThreadInfo(m.ThreadId, m.ParentThreadId, m.Status, m.Intent))
            .ToImmutableArray();
    }

    public string New(string parentThreadId, string message, InboxDelivery delivery)
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

        EnqueueFromThread(parentThreadId, id, message, delivery);
        return id;
    }

    public string Fork(string parentThreadId, SessionState parentState, string message, InboxDelivery delivery)
    {
        Debug.Assert(parentState.Buffer == TurnBuffer.Empty, "Fork requires empty buffer (no in-flight streaming deltas)");

        var childId = New(parentThreadId, message, delivery);

        // For now we do not persist cloned state; we will when we promote ThreadState to a first-class
        // persisted snapshot. At minimum, copy the committed events for readback.
        foreach (var evt in parentState.Committed)
            _store.AppendCommittedEvent(_sessionId, childId, evt);

        return childId;
    }

    public void Send(string fromThreadId, string toThreadId, string message, InboxDelivery delivery)
    {
        EnqueueFromThread(fromThreadId, toThreadId, message, delivery);
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

    private void EnqueueFromThread(string fromThreadId, string toThreadId, string message, InboxDelivery delivery)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        _store.AppendInbox(_sessionId, toThreadId, new ThreadEnvelope(
            Source: "thread",
            SourceThreadId: fromThreadId,
            Text: message,
            Delivery: delivery,
            EnqueuedAtIso: now));
    }
}
