using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolCallDeduplicationTests
{
    [Fact]
    public void WhenSameToolCallIdDetectedTwice_ThenSecondIsIgnored()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" } },
            required = new[] { "path" },
        });

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(new ToolDefinition("read_text_file", "", schema)),
        };

        var args = JsonSerializer.SerializeToElement(new { path = "/tmp/a.txt" });

        var first = Core.Reduce(state, new ObservedToolCallDetected("call_1", "read_text_file", args));
        first.NewlyCommitted.Should().ContainSingle(e => e is ToolCallRequested);

        var second = Core.Reduce(first.Next, new ObservedToolCallDetected("call_1", "read_text_file", args));
        second.NewlyCommitted.Should().BeEmpty();
        second.Effects.Should().BeEmpty();
    }
}
