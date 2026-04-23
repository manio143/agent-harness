using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionEventReducerTests
{
    [Fact]
    public void Reduce_ObservedThreadCompactedGenerated_CommitsThreadCompacted()
    {
        var state = SessionState.Empty with
        {
            Buffer = TurnBuffer.Empty with { CompactionDue = true },
        };

        var result = Core.Reduce(state, new ObservedThreadCompactedGenerated("main", "<compaction>hello</compaction>"));

        result.NewlyCommitted.Should().ContainSingle();
        result.NewlyCommitted[0].Should().BeOfType<ThreadCompacted>();

        var committed = (ThreadCompacted)result.NewlyCommitted[0];
        committed.Text.Should().Be("<compaction>hello</compaction>");

        result.Next.Buffer.CompactionDue.Should().BeFalse();
    }
}
