using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerTurnStartedFromIdleFlagTests
{
    [Fact]
    public void ObservedTurnStarted_WhenThreadWasIdle_SetsTurnStartedFromIdleTrue()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded()),
        };

        var result = Core.Reduce(state, new ObservedTurnStarted(ThreadIds.Main));

        result.Next.Buffer.TurnStartedFromIdle.Should().BeTrue();
        result.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<TurnStarted>();
    }

    [Fact]
    public void ObservedTurnStarted_WhenThreadWasRunning_SetsTurnStartedFromIdleFalse()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(new TurnStarted()),
        };

        var result = Core.Reduce(state, new ObservedTurnStarted(ThreadIds.Main));

        result.Next.Buffer.TurnStartedFromIdle.Should().BeFalse();
        result.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<TurnStarted>();
    }

    [Fact]
    public void ObservedTurnStarted_WhenNoPriorTurns_SetsTurnStartedFromIdleTrue()
    {
        var result = Core.Reduce(SessionState.Empty, new ObservedTurnStarted(ThreadIds.Main));

        result.Next.Buffer.TurnStartedFromIdle.Should().BeTrue();
        result.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<TurnStarted>();
    }
}
