using Agent.Acp.Acp;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;

namespace Agent.Harness.Threads;

/// <summary>
/// Publishes inbox lifecycle events as edge-visible ACP session/update payloads.
/// The committed session event log is the single source of truth; this publisher is best-effort.
/// </summary>
public sealed class AcpPublishingThreadEventRecorder : IThreadEventRecorder
{
    private readonly IAcpSessionEvents _events;

    public AcpPublishingThreadEventRecorder(IAcpSessionEvents events)
    {
        _events = events;
    }

    public void InboxEnqueued(ThreadEnvelope envelope, string threadId)
    {
        // Publish as a custom session/update; best-effort (no awaiting inside tool execution).
        _ = Task.Run(() => _events.SendSessionUpdateAsync(new Dictionary<string, object?>
        {
            ["kind"] = "thread_inbox_message_enqueued",
            ["threadId"] = threadId,
            ["envelopeId"] = envelope.EnvelopeId,
            ["messageKind"] = envelope.Kind.ToString(),
            ["meta"] = envelope.Meta,
            ["source"] = envelope.Source,
            ["sourceThreadId"] = envelope.SourceThreadId,
            ["delivery"] = envelope.Delivery.ToString().ToLowerInvariant(),
            ["enqueuedAtIso"] = envelope.EnqueuedAtIso,
            ["text"] = envelope.Text,
        }, CancellationToken.None));
    }

    public void InboxDeliveredToLlm(ThreadEnvelope envelope, string threadId)
    {
        _ = Task.Run(() => _events.SendSessionUpdateAsync(new Dictionary<string, object?>
        {
            ["kind"] = "thread_inbox_message_dequeued",
            ["threadId"] = threadId,
            ["envelopeId"] = envelope.EnvelopeId,
            ["dequeuedAtIso"] = DateTimeOffset.UtcNow.ToString("O"),
        }, CancellationToken.None));
    }
}
