using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class IntentPolicyTests
{
    [Fact]
    public void ToolCallDetected_WhenIntentNotReportedThisTurn_Rejected_And_Requests_ModelCall()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new UserMessage("do something")),
            Buffer: TurnBuffer.Empty with { IntentReportedThisTurn = false },
            Tools: ImmutableArray.Create(
                ToolSchemas.ReportIntent,
                ToolSchemas.ExecuteCommand));

        var observed = new ObservedToolCallDetected(
            ToolId: "call_1",
            ToolName: ToolSchemas.ExecuteCommand.Name,
            Args: new { command = "ls" });

        var result = Core.Reduce(state, observed);

        result.NewlyCommitted.OfType<ToolCallRequested>()
            .Should().ContainSingle(r => r.ToolId == "call_1" && r.ToolName == ToolSchemas.ExecuteCommand.Name);

        var rejected = result.NewlyCommitted.OfType<ToolCallRejected>().Single();
        rejected.ToolId.Should().Be("call_1");
        rejected.Reason.Should().Be("missing_report_intent");
        rejected.Details.Should().Contain("must_call:report_intent");

        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }

    [Fact]
    public void ToolCallDetected_AfterIntentReported_Allows_NextToolCall()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new UserMessage("do something")),
            Buffer: TurnBuffer.Empty with { IntentReportedThisTurn = true },
            Tools: ImmutableArray.Create(
                ToolSchemas.ReportIntent,
                ToolSchemas.ExecuteCommand));

        var observed = new ObservedToolCallDetected(
            ToolId: "call_2",
            ToolName: ToolSchemas.ExecuteCommand.Name,
            Args: new { command = "ls" });

        var result = Core.Reduce(state, observed);

        var requested = result.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<ToolCallRequested>().Which;
        requested.ToolId.Should().Be("call_2");
        requested.ToolName.Should().Be(ToolSchemas.ExecuteCommand.Name);

        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CheckPermission>();
    }
}
