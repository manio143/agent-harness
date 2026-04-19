using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerNewThreadTaskPromotionTests
{
    [Fact]
    public void ObservedInboxArrived_NewThreadTask_WhenWoken_PromotesToNewThreadTaskEvent_UsingMeta()
    {
        var state = SessionState.Empty;

        var envId = ThreadEnvelopes.NewEnvelopeId();
        var now = "2026-04-19T00:00:00Z";

        var meta = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["parentThreadId"] = "main",
            ["isFork"] = "true",
        });

        var arrived = new ObservedInboxMessageArrived(
            ThreadId: "thr_child",
            Kind: ThreadInboxMessageKind.NewThreadTask,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: envId,
            EnqueuedAtIso: now,
            Source: "thread",
            SourceThreadId: "main",
            Text: "do work",
            Meta: meta);

        var r1 = Core.Reduce(state, arrived);
        r1.NewlyCommitted.Should().ContainSingle(e => e is ThreadInboxMessageEnqueued);

        var r2 = Core.Reduce(r1.Next, new ObservedWakeModel("thr_child"));

        r2.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Any(d => d.EnvelopeId == envId).Should().BeTrue();
        var task = r2.NewlyCommitted.OfType<NewThreadTask>().Single();
        task.ThreadId.Should().Be("thr_child");
        task.ParentThreadId.Should().Be("main");
        task.IsFork.Should().BeTrue();
        task.Message.Should().Be("do work");
    }
}
