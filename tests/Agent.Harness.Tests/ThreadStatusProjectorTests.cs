using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadStatusProjectorTests
{
    [Fact]
    public void IsIdleAtWakeBoundary_IgnoresTrailingTurnStarted()
    {
        // Previous turn ended => idle.
        var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded());
        ThreadStatusProjector.IsIdleAtWakeBoundary(committed).Should().BeTrue();

        // Runner prepends a TurnStarted for the new run; wake should still treat as idle.
        committed = committed.Add(new TurnStarted());
        ThreadStatusProjector.IsIdleAtWakeBoundary(committed).Should().BeTrue();
    }

    [Fact]
    public void IsIdleAtWakeBoundary_IsFalse_WhenThereIsAnUnendedPriorTurn()
    {
        // Simulate a crash mid-turn: TurnStarted without TurnEnded in the log.
        // When the next run starts, SessionRunner prepends another TurnStarted.
        var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnStarted());

        ThreadStatusProjector.IsIdleAtWakeBoundary(committed).Should().BeFalse();
    }
}
