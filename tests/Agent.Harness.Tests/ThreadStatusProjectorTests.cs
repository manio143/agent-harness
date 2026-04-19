using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadStatusProjectorTests
{
    [Fact]
    public void ProjectStatus_WhenNoTurnStarted_IsIdle()
    {
        ThreadStatusProjector.ProjectStatus(ImmutableArray<SessionEvent>.Empty)
            .Should().Be(ThreadStatus.Idle);
    }

    [Fact]
    public void ProjectStatus_WhenStartedAndNotEnded_IsRunning()
    {
        var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted());

        ThreadStatusProjector.ProjectStatus(committed)
            .Should().Be(ThreadStatus.Running);
    }

    [Fact]
    public void ProjectStatus_WhenEndedAfterStarted_IsIdle()
    {
        var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded());

        ThreadStatusProjector.ProjectStatus(committed)
            .Should().Be(ThreadStatus.Idle);
    }

    [Fact]
    public void ProjectStatus_UsesLastStartedVsLastEnded()
    {
        // start, end, start -> running
        {
            var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded(), new TurnStarted());
            ThreadStatusProjector.ProjectStatus(committed).Should().Be(ThreadStatus.Running);
        }

        // start, start, end -> idle (end after last start)
        {
            var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnStarted(), new TurnEnded());
            ThreadStatusProjector.ProjectStatus(committed).Should().Be(ThreadStatus.Idle);
        }

        // start, end, end -> idle
        {
            var committed = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded(), new TurnEnded());
            ThreadStatusProjector.ProjectStatus(committed).Should().Be(ThreadStatus.Idle);
        }
    }

    [Fact]
    public void IsIdle_MatchesProjectedStatus()
    {
        ThreadStatusProjector.IsIdle(ImmutableArray.Create<SessionEvent>(new TurnStarted()))
            .Should().BeFalse();

        ThreadStatusProjector.IsIdle(ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded()))
            .Should().BeTrue();
    }

    [Fact]
    public void IsIdleAtWakeBoundary_IgnoresTrailingTurnStartedMarker()
    {
        // Under normal reducer semantics: if the thread had ended, it is idle.
        var baseCommitted = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnEnded());
        ThreadStatusProjector.IsIdleAtWakeBoundary(baseCommitted).Should().BeTrue();

        // SessionRunner may prepend TurnStarted before emitting wake events. If the last event is TurnStarted,
        // we should treat it as a wake-boundary marker and ignore it.
        var wakeCommitted = baseCommitted.Add(new TurnStarted());
        ThreadStatusProjector.IsIdleAtWakeBoundary(wakeCommitted).Should().BeTrue();

        // But if there is no TurnEnded, ignoring trailing TurnStarted still means running.
        var runningWake = ImmutableArray.Create<SessionEvent>(new TurnStarted(), new TurnStarted());
        ThreadStatusProjector.IsIdleAtWakeBoundary(runningWake).Should().BeFalse();
    }
}
