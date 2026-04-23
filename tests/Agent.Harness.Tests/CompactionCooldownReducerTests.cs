using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionCooldownReducerTests
{
    [Fact]
    public void Reduce_TurnStarted_AfterCompactionCommitted_SuppressesCompactionForThisTurn()
    {
        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new TurnEnded(),
                new CompactionCommitted(System.Text.Json.JsonSerializer.SerializeToElement(new { s = 1 }), "sum")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var started = Core.Reduce(state, new ObservedTurnStarted(Agent.Harness.Threads.ThreadIds.Main));

        started.Next.Buffer.CompactionSuppressedThisTurn.Should().BeTrue();
    }

    [Fact]
    public void Reduce_TokenUsage_WhenSuppressed_DoesNotLatchCompactionDue()
    {
        var opts = new CoreOptions(
            ContextWindowTokensByProviderModel: _ => 1000,
            CompactionThreshold: 0.01);

        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new TurnEnded(),
                new CompactionCommitted(System.Text.Json.JsonSerializer.SerializeToElement(new { s = 1 }), "sum"),
                new TurnStarted()),
            Buffer: TurnBuffer.Empty with { CompactionSuppressedThisTurn = true },
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var reduced = Core.Reduce(state, new ObservedTokenUsage(InputTokens: 1, OutputTokens: 2, TotalTokens: 100, ProviderModel: "p"), opts);

        reduced.Next.Buffer.CompactionDue.Should().BeFalse();
    }
}
