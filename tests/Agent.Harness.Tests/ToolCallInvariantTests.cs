using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolCallInvariantTests
{
    [Fact]
    public void CoreReducer_NeverCommits_ToolCallRejected_Without_Prior_ToolCallRequested_InCommitted()
    {
        // WHY THIS IS AN INVARIANT:
        // Rejections must be auditable. When replaying events.jsonl we need to know what the model
        // attempted to call (tool name + args) even if the call was rejected.

        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("do something"),
                // NOTE: no ToolCallRequested("call_1", ...)
                new ToolCallPermissionDenied("call_1", "nope")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var observed = new ObservedPermissionDenied(
            ToolId: "call_1",
            Reason: "nope");

        var result = Core.Reduce(state, observed);

        result.NewlyCommitted.OfType<ToolCallRejected>().Should().BeEmpty();
    }
}
