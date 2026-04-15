using Agent.Acp.Acp;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;

namespace Agent.Harness.Threads;

/// <summary>
/// Records inbox lifecycle events by appending committed events to the session store,
/// and also publishing an edge-visible ACP session/update payload.
/// </summary>
public sealed class AcpPublishingThreadEventRecorder : IThreadEventRecorder
{
    private readonly string _sessionId;
    private readonly ISessionStore _store;
    private readonly IAcpSessionEvents _events;

    public AcpPublishingThreadEventRecorder(string sessionId, ISessionStore store, IAcpSessionEvents events)
    {
        _sessionId = sessionId;
        _store = store;
        _events = events;
    }

    public void InboxEnqueued(ThreadEnvelope envelope, string threadId)
    {
        var evt = new ThreadInboxMessageEnqueued(
            ThreadId: threadId,
            EnvelopeId: envelope.EnvelopeId,
            Source: envelope.Source,
            SourceThreadId: envelope.SourceThreadId,
            Delivery: envelope.Delivery.ToString().ToLowerInvariant(),
            EnqueuedAtIso: envelope.EnqueuedAtIso,
            Text: envelope.Text);

        _store.AppendCommitted(_sessionId, evt);

        // Publish as a custom session/update; best-effort (no awaiting inside tool execution).
        _ = Task.Run(() => _events.SendSessionUpdateAsync(new Dictionary<string, object?>
        {
            ["kind"] = "thread_inbox_message_enqueued",
            ["threadId"] = evt.ThreadId,
            ["envelopeId"] = evt.EnvelopeId,
            ["source"] = evt.Source,
            ["sourceThreadId"] = evt.SourceThreadId,
            ["delivery"] = evt.Delivery,
            ["enqueuedAtIso"] = evt.EnqueuedAtIso,
            ["text"] = evt.Text,
        }, CancellationToken.None));
    }

    public void InboxDeliveredToLlm(ThreadEnvelope envelope, string threadId)
    {
        var evt = new ThreadInboxMessageDeliveredToLlm(
            ThreadId: threadId,
            EnvelopeId: envelope.EnvelopeId,
            DeliveredAtIso: DateTimeOffset.UtcNow.ToString("O"));

        _store.AppendCommitted(_sessionId, evt);

        _ = Task.Run(() => _events.SendSessionUpdateAsync(new Dictionary<string, object?>
        {
            ["kind"] = "thread_inbox_message_delivered_to_llm",
            ["threadId"] = evt.ThreadId,
            ["envelopeId"] = evt.EnvelopeId,
            ["deliveredAtIso"] = evt.DeliveredAtIso,
        }, CancellationToken.None));
    }
}
