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

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

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

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }

    [Fact]
    public void HasDeliverableEnqueueNow_OnlyTrue_WhenThreadIdle()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        // Create child metadata directly in store (thread lifecycle owned by orchestrator).
        var child = "thr_test";
        threadStore.CreateThread("s1", new ThreadMetadata(
            ThreadId: child,
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: null));

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

        // Thread considered Running when latest turn marker is TurnStarted without TurnEnded.
        threadStore.AppendCommittedEvent("s1", child, new TurnStarted());
        mgr.HasDeliverableEnqueueNow(child).Should().BeFalse();

        threadStore.AppendCommittedEvent("s1", child, new TurnEnded());
        mgr.HasDeliverableEnqueueNow(child).Should().BeTrue();

        // Promotion/dequeue is reducer-driven now; ThreadManager no longer drains.
        // (We assert gating only: deliverable depends on idle status + enqueue presence.)
    }
}
