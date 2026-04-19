using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerInboxPromotionTests
{
    [Fact]
    public void ObservedInboxArrived_WhenWoken_ModelPromotesPendingInboxToFirstClassEvents()
    {
        var state = SessionState.Empty;

        var envId = ThreadEnvelopes.NewEnvelopeId();
        var now = "2026-04-15T00:00:00Z";
        var meta = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["k"] = "v",
        });

        var arrived = new ObservedInboxMessageArrived(
            ThreadId: ThreadIds.Main,
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: envId,
            EnqueuedAtIso: now,
            Source: "thread",
            SourceThreadId: "thr_a",
            Text: "hello",
            Meta: meta);

        var r1 = Core.Reduce(state, arrived);
        var enq = r1.NewlyCommitted.OfType<ThreadInboxMessageEnqueued>().Single();
        enq.Delivery.Should().Be(ThreadInboxDeliveryText.Immediate);

        var r2 = Core.Reduce(r1.Next, new ObservedWakeModel(ThreadIds.Main));
        r2.NewlyCommitted.Any(e => e is ThreadInboxMessageDequeued d && d.EnvelopeId == envId).Should().BeTrue();
        r2.NewlyCommitted.Any(e => e is InterThreadMessage it && it.FromThreadId == "thr_a" && it.Text == "hello").Should().BeTrue();
    }

    [Fact]
    public void ObservedInboxArrived_IdleNotification_PromotesToThreadIdleNotification_UsingMeta()
    {
        var state = SessionState.Empty;

        var envId = ThreadEnvelopes.NewEnvelopeId();
        var now = "2026-04-15T00:00:00Z";
        var meta = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            [ThreadInboxMetaKeys.ChildThreadId] = "thr_child",
            [ThreadInboxMetaKeys.LastIntent] = "doing work",
        });

        var arrived = new ObservedInboxMessageArrived(
            ThreadId: ThreadIds.Main,
            Kind: ThreadInboxMessageKind.ThreadIdleNotification,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: envId,
            EnqueuedAtIso: now,
            Source: "thread",
            SourceThreadId: "thr_child",
            Text: "Child became idle",
            Meta: meta);

        var r1 = Core.Reduce(state, arrived);
        var r2 = Core.Reduce(r1.Next, new ObservedWakeModel(ThreadIds.Main));

        r2.NewlyCommitted.Any(e => e is ThreadIdleNotification n && n.ChildThreadId == "thr_child" && n.LastIntent == "doing work").Should().BeTrue();
    }
}
