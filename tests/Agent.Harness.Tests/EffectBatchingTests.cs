using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class EffectBatchingTests
{
    [Fact]
    public void DeduplicateEffects_Dedupes_CallModel_WithinBatch()
    {
        var effects = ImmutableArray.Create<Effect>(
            new CallModel("default"),
            new CallModel("default"),
            new CheckPermission("call_1", "read_text_file", new { path = "/tmp/a.txt" }),
            new CallModel("default"));

        var deduped = TurnRunner.DeduplicateEffects(effects);

        // We should keep one CallModel and preserve relative ordering of the remaining effects.
        deduped.Select(e => e.GetType()).Should().ContainInOrder(
            typeof(CallModel),
            typeof(CheckPermission));

        deduped.Count(e => e is CallModel).Should().Be(1);
    }
}
