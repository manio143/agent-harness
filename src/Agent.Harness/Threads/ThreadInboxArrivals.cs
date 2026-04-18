using System;
using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public static class ThreadInboxArrivals
{
    public static ObservedInboxMessageArrived UserPrompt(
        string threadId,
        string text,
        string source,
        string? sourceThreadId = null,
        InboxDelivery delivery = InboxDelivery.Immediate,
        ImmutableDictionary<string, string>? meta = null)
        => new(
            ThreadId: threadId,
            Kind: ThreadInboxMessageKind.UserPrompt,
            Delivery: delivery,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: source,
            SourceThreadId: sourceThreadId,
            Text: text,
            Meta: meta);

    public static ObservedInboxMessageArrived InterThreadMessage(
        string threadId,
        string text,
        string sourceThreadId,
        string source = "thread",
        InboxDelivery delivery = InboxDelivery.Immediate,
        ImmutableDictionary<string, string>? meta = null)
        => new(
            ThreadId: threadId,
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Delivery: delivery,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: source,
            SourceThreadId: sourceThreadId,
            Text: text,
            Meta: meta);

    public static ObservedInboxMessageArrived ThreadIdleNotification(
        string parentThreadId,
        string childThreadId,
        string lastIntent,
        InboxDelivery delivery = InboxDelivery.Immediate)
        => new(
            ThreadId: parentThreadId,
            Kind: ThreadInboxMessageKind.ThreadIdleNotification,
            Delivery: delivery,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: "thread",
            SourceThreadId: childThreadId,
            Text: $"Child thread became idle. Last intent: {lastIntent}",
            Meta: ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, string>("childThreadId", childThreadId),
                new KeyValuePair<string, string>("lastIntent", lastIntent),
            }));
}
