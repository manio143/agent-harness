using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerThreadScopedWakeTests
{
    [Fact]
    public void ObservedWakeModel_OnlyPromotesInboxForTheTargetThread()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                // main inbox item
                new ThreadInboxMessageEnqueued(
                    ThreadId: ThreadIds.Main,
                    EnvelopeId: "env_main",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_a",
                    Delivery: "immediate",
                    EnqueuedAtIso: "t0",
                    Text: "hello main"),

                // other thread inbox item
                new ThreadInboxMessageEnqueued(
                    ThreadId: "thr_other",
                    EnvelopeId: "env_other",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_a",
                    Delivery: "immediate",
                    EnqueuedAtIso: "t0",
                    Text: "hello other")
            )
        };

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

        // Should dequeue/promote only env_main.
        result.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Should().ContainSingle(d => d.EnvelopeId == "env_main");
        result.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Any(d => d.EnvelopeId == "env_other").Should().BeFalse();
    }
}
