using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionBoundaryReducerTests
{
    [Fact]
    public void Reduce_TurnStabilized_WhenCompactionDue_EndsTurnAndSchedulesCompaction()
    {
        // ARRANGE: start a turn and mark compaction due.
        var s0 = SessionState.Empty;
        var s1 = Core.Reduce(s0, new ObservedTurnStarted(ThreadIds.Main)).Next;
        var s2 = s1 with { Buffer = s1.Buffer with { CompactionDue = true } };

        // ACT
        var result = Core.Reduce(s2, new ObservedTurnStabilized(ThreadIds.Main));

        // ASSERT: TurnEnded is committed and compaction is scheduled.
        result.NewlyCommitted.Should().Contain(e => e is TurnEnded);
        result.Effects.Should().ContainSingle(e => e is RunCompaction);
        result.Effects.OfType<RunCompaction>().Single().ThreadId.Should().Be(ThreadIds.Main);

        // And we must not call the model in the same turn.
        result.Effects.Should().NotContain(e => e is CallModel);
    }
}
