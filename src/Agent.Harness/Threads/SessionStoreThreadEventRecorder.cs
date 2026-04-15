using Agent.Harness.Persistence;

namespace Agent.Harness.Threads;

public sealed class SessionStoreThreadEventRecorder : IThreadEventRecorder
{
    private readonly string _sessionId;
    private readonly ISessionStore _store;

    public SessionStoreThreadEventRecorder(string sessionId, ISessionStore store)
    {
        _sessionId = sessionId;
        _store = store;
    }

    public void InboxEnqueued(ThreadEnvelope envelope, string threadId)
    {
        _store.AppendCommitted(_sessionId, new ThreadInboxMessageEnqueued(
            ThreadId: threadId,
            EnvelopeId: envelope.EnvelopeId,
            Source: envelope.Source,
            SourceThreadId: envelope.SourceThreadId,
            Delivery: envelope.Delivery.ToString().ToLowerInvariant(),
            EnqueuedAtIso: envelope.EnqueuedAtIso,
            Text: envelope.Text));
    }

    public void InboxDeliveredToLlm(ThreadEnvelope envelope, string threadId)
    {
        _store.AppendCommitted(_sessionId, new ThreadInboxMessageDequeued(
            ThreadId: threadId,
            EnvelopeId: envelope.EnvelopeId,
            DequeuedAtIso: DateTimeOffset.UtcNow.ToString("O")));
    }
}
