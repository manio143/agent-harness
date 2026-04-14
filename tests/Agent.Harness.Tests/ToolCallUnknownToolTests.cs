using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolCallUnknownToolTests
{
    [Fact]
    public void ToolCallDetected_WhenToolIsUnknown_Commits_Rejected_And_Requests_ModelCall()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new UserMessage("do something")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(
                new ToolDefinition("read_text_file", "", JsonSerializer.SerializeToElement(new { type = "object" }))));

        var observed = new ObservedToolCallDetected(
            ToolId: "call_1",
            ToolName: "hallucinated_tool",
            Args: new { foo = "bar" });

        var result = Core.Reduce(state, observed);

        var rejected = result.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<ToolCallRejected>().Which;
        rejected.ToolId.Should().Be("call_1");
        rejected.Reason.Should().Be("unknown_tool");
        rejected.Details.Should().Equal("unknown_tool");

        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }
}
