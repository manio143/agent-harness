using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ReportIntentRePromptTests
{
    [Fact]
    public void ToolCallCompleted_ForReportIntent_WhenNoOtherOpenTools_Requests_ModelCall()
    {
        // Arrange: report_intent requested in the same turn, and then completed.
        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("do"),
                new ToolCallRequested("call_0", ToolSchemas.ReportIntent.Name, System.Text.Json.JsonSerializer.SerializeToElement(new { intent = "x" }))),
            Buffer: TurnBuffer.Empty with { IntentReportedThisTurn = true },
            Tools: ImmutableArray.Create(ToolSchemas.ReportIntent));

        // Act
        var result = Core.Reduce(state, new ObservedToolCallCompleted("call_0", new { ok = true }));

        // Assert
        result.NewlyCommitted.Should().HaveCount(2);
        result.NewlyCommitted[0].Should().BeOfType<ToolCallCompleted>();
        result.NewlyCommitted[1].Should().BeOfType<ThreadIntentReported>();
        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }
}
