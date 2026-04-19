using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class DeduplicateEffectsUnknownEffectTests
{
    private sealed record SomeNewEffect(string Value) : Effect;

    [Fact]
    public void DeduplicateEffects_WhenEffectTypeIsUnknown_UsesTypeNameKeyForDeduping()
    {
        var a1 = new SomeNewEffect("a");
        var a2 = new SomeNewEffect("b");

        var deduped = TurnRunner.DeduplicateEffects(ImmutableArray.Create<Effect>(a1, a2));

        // Same runtime type => same key => deduped to first.
        deduped.Should().HaveCount(1);
        deduped[0].Should().Be(a1);
    }
}
