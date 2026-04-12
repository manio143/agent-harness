using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ReasoningCommitModeTests
{
    [Fact]
    public void WhenCommitReasoningTextDeltasDisabled_ReasoningIsNotCommitted()
    {
        var result = Core.Reduce(
            SessionState.Empty,
            new ObservedReasoningTextDelta("think"),
            new CoreOptions(CommitReasoningTextDeltas: false));

        result.NewlyCommitted.Should().BeEmpty();
    }

    [Fact]
    public void WhenCommitReasoningTextDeltasEnabled_ReasoningIsCommittedImmediately()
    {
        var result = Core.Reduce(
            SessionState.Empty,
            new ObservedReasoningTextDelta("think"),
            new CoreOptions(CommitReasoningTextDeltas: true));

        result.NewlyCommitted.Should().ContainSingle().Which.Should().Be(new ReasoningTextDelta("think"));
    }
}
