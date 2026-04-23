using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerTokenUsageProviderModelTests
{
    [Fact]
    public void Reduce_ObservedTokenUsage_CommitsProviderModel()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedTokenUsage(1, 2, 3, "qwen2.5:3b"));

        result.NewlyCommitted.Should().ContainSingle();
        var usage = result.NewlyCommitted[0].Should().BeOfType<TokenUsage>().Subject;
        usage.ProviderModel.Should().Be("qwen2.5:3b");
    }
}
