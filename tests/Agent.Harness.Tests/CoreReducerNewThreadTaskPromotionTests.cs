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

    [Fact]
    public void ObservedInboxArrived_NewThreadTask_WhenAlreadyPresent_DequeuesButDoesNotCommitDuplicateMarker()
    {
        var existing = new NewThreadTask(ThreadId: "thr_child", ParentThreadId: "main", IsFork: true, Message: "first");
        var state = SessionState.Empty with { Committed = ImmutableArray.Create<SessionEvent>(existing) };

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
            Text: "second",
            Meta: meta);

        var r1 = Core.Reduce(state, arrived);
        var r2 = Core.Reduce(r1.Next, new ObservedWakeModel("thr_child"));

        r2.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Any(d => d.EnvelopeId == envId).Should().BeTrue();
        r2.NewlyCommitted.OfType<NewThreadTask>().Should().BeEmpty();

        // Still only the original marker.
        r2.Next.Committed.OfType<NewThreadTask>().Should().ContainSingle().Which.Message.Should().Be("first");
    }

    [Fact]
    public void ObservedInboxArrived_NewThreadTask_WhenMetaMissing_FallsBackToSourceThreadId_AndNotFork()
    {
        var state = SessionState.Empty;

        var envId = ThreadEnvelopes.NewEnvelopeId();
        var now = "2026-04-19T00:00:00Z";

        var arrived = new ObservedInboxMessageArrived(
            ThreadId: "thr_child",
            Kind: ThreadInboxMessageKind.NewThreadTask,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: envId,
            EnqueuedAtIso: now,
            Source: "thread",
            SourceThreadId: "main",
            Text: "do work",
            Meta: null);

        var r1 = Core.Reduce(state, arrived);
        var r2 = Core.Reduce(r1.Next, new ObservedWakeModel("thr_child"));

        var task = r2.NewlyCommitted.OfType<NewThreadTask>().Single();
        task.ParentThreadId.Should().Be("main");
        task.IsFork.Should().BeFalse();
    }
}
