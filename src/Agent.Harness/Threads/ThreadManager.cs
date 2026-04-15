using System.Collections.Immutable;
using System.Diagnostics;
using Agent.Harness.Persistence;

namespace Agent.Harness.Threads;

public sealed class ThreadManager
{
    public bool HasDeliverableEnqueueNow(string threadId)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta?.Status != ThreadStatus.Idle)
            return false;

        var items = LoadPendingInbox(threadId);
        return items.Any(e => e.Delivery == InboxDelivery.Enqueue);
    }

    public bool HasImmediateOrDeliverableEnqueue(string threadId)
    {
        var items = LoadPendingInbox(threadId);
        if (items.IsDefaultOrEmpty) return false;

        if (items.Any(i => i.Delivery == InboxDelivery.Immediate))
            return true;

        return HasDeliverableEnqueueNow(threadId);
    }

    private readonly string _sessionId;
    private readonly IThreadStore _store;
    private readonly Persistence.ISessionStore _sessionStore;
    private readonly IThreadEventRecorder? _events;

    public ThreadManager(string sessionId, IThreadStore store, Persistence.ISessionStore sessionStore, IThreadEventRecorder? events = null)
    {
        _sessionId = sessionId;
        _store = store;
        _sessionStore = sessionStore;
        _events = events;
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

    public ImmutableArray<ThreadEnvelope> DrainInboxForPrompt(string threadId)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        var isIdle = meta?.Status == ThreadStatus.Idle;

        var items = LoadPendingInbox(threadId);
        if (items.IsDefaultOrEmpty)
            return ImmutableArray<ThreadEnvelope>.Empty;

        var deliver = ImmutableArray.CreateBuilder<ThreadEnvelope>();

        foreach (var env in items.OrderBy(e => e.EnqueuedAtIso).ThenBy(e => e.EnvelopeId))
        {
            // immediate: always deliver on the next prompt
            if (env.Delivery == InboxDelivery.Immediate)
            {
                deliver.Add(env);
                continue;
            }

            // enqueue: only becomes eligible once the target thread is idle
            if (env.Delivery == InboxDelivery.Enqueue && isIdle)
            {
                deliver.Add(env);
                continue;
            }
        }

        // Commit "taken out of inbox" marker for each envelope that is made available to prompt rendering.
        foreach (var env in deliver)
        {
            var evt = new ThreadInboxMessageDequeued(
                ThreadId: threadId,
                EnvelopeId: env.EnvelopeId,
                DequeuedAtIso: DateTimeOffset.UtcNow.ToString("O"));

            _sessionStore.AppendCommitted(_sessionId, evt);
            _events?.InboxDeliveredToLlm(env, threadId);
        }

        return deliver.ToImmutable();
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

    public string New(string parentThreadId, string message, InboxDelivery delivery)
    {
        var id = CreateChildThread(parentThreadId);
        EnqueueFromThread(parentThreadId, id, message, delivery, ThreadInboxMessageKind.InterThreadMessage, meta: null);
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

    public string Fork(string parentThreadId, SessionState parentState, string message, InboxDelivery delivery)
    {
        var childId = ForkChildThread(parentThreadId, parentState);
        EnqueueFromThread(parentThreadId, childId, message, delivery, ThreadInboxMessageKind.InterThreadMessage, meta: null);
        return childId;
    }

    public void Send(
        string fromThreadId,
        string toThreadId,
        string message,
        InboxDelivery delivery,
        ThreadInboxMessageKind kind = ThreadInboxMessageKind.InterThreadMessage,
        ImmutableDictionary<string, string>? meta = null)
    {
        EnqueueFromThread(fromThreadId, toThreadId, message, delivery, kind, meta);
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

    private void EnqueueFromThread(
        string fromThreadId,
        string toThreadId,
        string message,
        InboxDelivery delivery,
        ThreadInboxMessageKind kind,
        ImmutableDictionary<string, string>? meta)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var env = new ThreadEnvelope(
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            Kind: kind,
            Meta: meta,
            Source: "thread",
            SourceThreadId: fromThreadId,
            Text: message,
            Delivery: delivery,
            EnqueuedAtIso: now);

        var evt = new ThreadInboxMessageEnqueued(
            ThreadId: toThreadId,
            EnvelopeId: env.EnvelopeId,
            Kind: env.Kind,
            Meta: env.Meta,
            Source: env.Source,
            SourceThreadId: env.SourceThreadId,
            Delivery: env.Delivery.ToString().ToLowerInvariant(),
            EnqueuedAtIso: env.EnqueuedAtIso,
            Text: env.Text);

        _sessionStore.AppendCommitted(_sessionId, evt);
        _events?.InboxEnqueued(env, toThreadId);
    }

    private ImmutableArray<ThreadEnvelope> LoadPendingInbox(string threadId)
    {
        var committed = _sessionStore.LoadCommitted(_sessionId);

        // Project the inbox from committed events (single source of truth).
        var enqueued = committed
            .OfType<ThreadInboxMessageEnqueued>()
            .Where(e => e.ThreadId == threadId)
            .Select(e => new ThreadEnvelope(
                EnvelopeId: e.EnvelopeId,
                Kind: e.Kind,
                Meta: e.Meta,
                Source: e.Source,
                SourceThreadId: e.SourceThreadId,
                Text: e.Text,
                Delivery: e.Delivery == "enqueue" ? InboxDelivery.Enqueue : InboxDelivery.Immediate,
                EnqueuedAtIso: e.EnqueuedAtIso))
            .ToImmutableArray();

        if (enqueued.IsDefaultOrEmpty)
            return ImmutableArray<ThreadEnvelope>.Empty;

        var deliveredIds = committed
            .OfType<ThreadInboxMessageDequeued>()
            .Where(d => d.ThreadId == threadId)
            .Select(d => d.EnvelopeId)
            .ToHashSet(StringComparer.Ordinal);

        return enqueued.Where(e => !deliveredIds.Contains(e.EnvelopeId)).ToImmutableArray();
    }
}
