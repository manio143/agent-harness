using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionContinuationTurnStartReducerTests
{
    [Fact]
    public void Reduce_TurnStarted_WhenContinuationPending_SetsIntentReportedThisTurnTrue()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty,
            Buffer: TurnBuffer.Empty with { ContinuationPending = true },
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var reduced = Core.Reduce(state, new ObservedTurnStarted(Agent.Harness.Threads.ThreadIds.Main));

        reduced.Next.Buffer.IntentReportedThisTurn.Should().BeTrue();
    }
}
