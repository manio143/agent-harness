using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ForkSeedBuilderTests
{
    [Fact]
    public void BuildForkSeed_IncludesOnlyCompleteTurns_AndDropsOpenToolCallsFromLastIncludedTurn()
    {
        var committed = ImmutableArray.Create<SessionEvent>(
            // Turn 1 complete
            new TurnStarted(),
            new UserMessage("hi"),
            new ToolCallRequested("t1", "read_text_file", default),
            new ToolCallCompleted("t1", default),
            new TurnEnded(),

            // Turn 2 complete
            new TurnStarted(),
            new UserMessage("done"),
            new TurnEnded(),

            // Turn 3 in progress (last round)
            new TurnStarted(),
            new UserMessage("in flight"),
            new ToolCallRequested("t3", "thread_start", default));

        var seed = ForkSeedBuilder.BuildForkSeed(committed);

        // Should end on a turn boundary
        seed.Last().Should().BeOfType<TurnEnded>();

        // Should NOT include unfulfilled tool call from the last round (Turn 3), but should keep the user message
        seed.OfType<ToolCallRequested>().Should().NotContain(r => r.ToolId == "t3");
        seed.OfType<UserMessage>().Should().ContainSingle(m => m.Text == "in flight");

        // Turn 1 tool call remains (complete)
        seed.OfType<ToolCallRequested>().Should().ContainSingle(r => r.ToolId == "t1");
        seed.OfType<ToolCallCompleted>().Should().ContainSingle(c => c.ToolId == "t1");
    }
}
