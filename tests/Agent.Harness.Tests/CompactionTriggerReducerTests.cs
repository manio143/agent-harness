using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionTriggerReducerTests
{
    [Fact]
    public void Reduce_ObservedTokenUsage_WhenThresholdCrossedAndLimitKnown_LatchesCompactionDue()
    {
        var state = SessionState.Empty;

        var options = new CoreOptions(
            ContextWindowTokensByProviderModel: _ => 4_000,
            CompactionThreshold: 0.90);

        var result = Core.Reduce(state, new ObservedTokenUsage(1, 1, 3_600, ProviderModel: "qwen2.5:3b"), options);

        result.Next.Buffer.CompactionDue.Should().BeTrue();
    }

    [Fact]
    public void Reduce_ObservedTokenUsage_WhenLimitUnknown_DoesNotLatchCompactionDue()
    {
        var state = SessionState.Empty;

        var options = new CoreOptions(
            ContextWindowTokensByProviderModel: _ => null,
            CompactionThreshold: 0.90);

        var result = Core.Reduce(state, new ObservedTokenUsage(1, 1, 10_000, ProviderModel: "unknown"), options);

        result.Next.Buffer.CompactionDue.Should().BeFalse();
    }

    [Fact]
    public void Reduce_ObservedTokenUsage_WhenBelowThreshold_DoesNotLatchCompactionDue()
    {
        var state = SessionState.Empty;

        var options = new CoreOptions(
            ContextWindowTokensByProviderModel: _ => 4_000,
            CompactionThreshold: 0.90);

        var result = Core.Reduce(state, new ObservedTokenUsage(1, 1, 3_599, ProviderModel: "qwen2.5:3b"), options);

        result.Next.Buffer.CompactionDue.Should().BeFalse();
    }
}
