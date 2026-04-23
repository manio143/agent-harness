using System.Text.Json;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionEventReducerTests
{
    [Fact]
    public void Reduce_ObservedCompactionGenerated_CommitsCompactionCommitted()
    {
        var state = SessionState.Empty;

        var payload = JsonSerializer.SerializeToElement(new
        {
            facts = new[] { "x" },
            proseSummary = "hello",
        });

        var result = Core.Reduce(state, new ObservedCompactionGenerated(payload, "hello"));

        result.NewlyCommitted.Should().ContainSingle();
        result.NewlyCommitted[0].Should().BeOfType<CompactionCommitted>();

        var committed = (CompactionCommitted)result.NewlyCommitted[0];
        committed.Structured.GetProperty("facts")[0].GetString().Should().Be("x");
        committed.ProseSummary.Should().Be("hello");
    }
}
