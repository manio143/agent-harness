using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerInboxDeliveryGatingTests
{
    [Fact]
    public void ObservedWakeModel_WhenThreadRunning_DoesNotPromote_Enqueue_Delivery()
    {
        // Thread is Running: started but not ended.
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new ThreadInboxMessageEnqueued(
                    ThreadId: ThreadIds.Main,
                    EnvelopeId: "env_1",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_x",
                    Delivery: "enqueue",
                    EnqueuedAtIso: "t0",
                    Text: "hello"))
        };

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void ObservedWakeModel_WhenThreadIdle_Promotes_Enqueue_Delivery()
    {
        // Thread is Idle: started then ended.
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new TurnEnded(),
                new ThreadInboxMessageEnqueued(
                    ThreadId: ThreadIds.Main,
                    EnvelopeId: "env_1",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_x",
                    Delivery: "enqueue",
                    EnqueuedAtIso: "t0",
                    Text: "hello"))
        };

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

        result.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Should().ContainSingle(d => d.EnvelopeId == "env_1");
        result.NewlyCommitted.OfType<InterThreadMessage>().Should().ContainSingle(m => m.Text == "hello");
        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }

    [Fact]
    public void ObservedWakeModel_WhenThreadRunning_StillPromotes_Immediate_Delivery()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new ThreadInboxMessageEnqueued(
                    ThreadId: ThreadIds.Main,
                    EnvelopeId: "env_1",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_x",
                    Delivery: "immediate",
                    EnqueuedAtIso: "t0",
                    Text: "hello"))
        };

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

        result.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Should().ContainSingle(d => d.EnvelopeId == "env_1");
        result.NewlyCommitted.OfType<InterThreadMessage>().Should().ContainSingle(m => m.Text == "hello");
        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }
}
