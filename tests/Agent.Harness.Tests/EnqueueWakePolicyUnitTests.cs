using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class EnqueueWakePolicyUnitTests
{
    [Fact]
    public void ObservedWakeModel_DoesNotEmit_CallModel_WhenNoInboxWasPromoted()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedWakeModel());

        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void ObservedWakeModel_Emits_CallModel_WhenInboxWasPromoted()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new ThreadInboxMessageEnqueued(
                    ThreadId: ThreadIds.Main,
                    EnvelopeId: "env_1",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_a",
                    Delivery: "immediate",
                    EnqueuedAtIso: "t0",
                    Text: "hi"))
        };

        var result = Core.Reduce(state, new ObservedWakeModel());

        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }

    [Fact]
    public void HasDeliverableEnqueueNow_OnlyTrue_WhenThreadIdle()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        var child = mgr.CreateChildThread(ThreadIds.Main);
        threadStore.AppendCommittedEvent("s1", child, new ThreadInboxMessageEnqueued(
            ThreadId: child,
            EnvelopeId: "env_1",
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Meta: null,
            Source: "thread",
            SourceThreadId: ThreadIds.Main,
            Delivery: "enqueue",
            EnqueuedAtIso: "t0",
            Text: "hello"));

        // Child defaults to idle.
        mgr.HasDeliverableEnqueueNow(child).Should().BeTrue();

        mgr.MarkRunning(child);
        mgr.HasDeliverableEnqueueNow(child).Should().BeFalse();

        mgr.MarkIdle(child);
        mgr.HasDeliverableEnqueueNow(child).Should().BeTrue();

        // Drain while idle -> clears
        _ = mgr.DrainInboxForPrompt(child);
        mgr.HasDeliverableEnqueueNow(child).Should().BeFalse();
    }
}
