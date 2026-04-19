using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class DeduplicateEffectsKnownKeysTests
{
    [Fact]
    public void DeduplicateEffects_WhenDefaultOrEmpty_ReturnsEmpty()
    {
        TurnRunner.DeduplicateEffects(default).Should().BeEmpty();
        TurnRunner.DeduplicateEffects(ImmutableArray<Effect>.Empty).Should().BeEmpty();
    }

    [Fact]
    public void DeduplicateEffects_CoversAllKnownKeyShapes_AndDedupesCorrectly()
    {
        var effects = ImmutableArray.Create<Effect>(
            new CheckPermission("p1", "t", new { }),
            new ExecuteToolCall("t1", "t", new { }),
            new CallModel("default"),
            new ScheduleWake("thr_x"),
            new CallModel("alt") // should dedupe by key "call_model"
        );

        var deduped = TurnRunner.DeduplicateEffects(effects);

        deduped.Select(e => e.GetType().Name).Should().Contain(new[]
        {
            nameof(CheckPermission),
            nameof(ExecuteToolCall),
            nameof(CallModel),
            nameof(ScheduleWake),
        });

        deduped.Count(e => e is CallModel).Should().Be(1);
    }
}
