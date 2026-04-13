using System.Collections.Immutable;
using Agent.Harness;

namespace Agent.Harness.Tests;

public class ToolCallArgValidationTests
{
    [Fact]
    public void ToolCallDetected_WithMissingRequiredArg_IsRejected_WithDetails_And_Requests_ModelCall()
    {
        // WHY THIS IS AN INVARIANT:
        // Tool availability is ephemeral per session; the core must be able to reject invalid calls
        // deterministically before any permission gating or execution happens.

        var initial = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty,
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        // read_text_file requires { path: string }
        var observed = new ObservedToolCallDetected(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: new { });

        var result = Core.Reduce(initial, observed);

        var rejected = Assert.Single(result.NewlyCommitted.OfType<ToolCallRejected>());
        Assert.Equal("invalid_args", rejected.Reason);
        Assert.Contains("missing_required:path", rejected.Details);

        Assert.Single(result.Effects.OfType<CallModel>());
    }

    [Fact]
    public void ToolCallDetected_WithTypeMismatch_IsRejected_WithDetails_And_Requests_ModelCall()
    {
        var initial = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty,
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var observed = new ObservedToolCallDetected(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: new { path = 123 });

        var result = Core.Reduce(initial, observed);

        var rejected = Assert.Single(result.NewlyCommitted.OfType<ToolCallRejected>());
        Assert.Equal("invalid_args", rejected.Reason);
        Assert.Contains("type_mismatch:path:string", rejected.Details);

        Assert.Single(result.Effects.OfType<CallModel>());
    }
}
