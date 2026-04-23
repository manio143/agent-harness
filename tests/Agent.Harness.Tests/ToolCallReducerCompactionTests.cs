using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolCallReducerCompactionTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ToolCallCompleted_WhenCompactionDue_DoesNotCallModel_AndLatchesContinuationPending()
    {
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Do it"),
                new ToolCallRequested("call_1", "read_text_file", J(new { path = "/tmp/test.txt" })),
                new ToolCallPending("call_1"),
                new ToolCallInProgress("call_1")),
            Buffer: TurnBuffer.Empty with { CompactionDue = true },
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var result = Core.Reduce(initial, new ObservedToolCallCompleted("call_1", new { ok = true }));

        result.Effects.Should().NotContain(e => e is CallModel);
        result.Next.Buffer.ContinuationPending.Should().BeTrue();
    }
}
