using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class TurnRunnerDriftPreventionTests
{
    [Fact]
    public async Task RunAsync_And_RunWithEffectsAsync_ProduceSameCommittedEvents_WhenEffectsAreNoOp()
    {
        var initial = SessionState.Empty;

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            // No user message here: keep the reducer in a no-effect path so we can compare
            // reduce-only vs reduce+effects for drift.
            yield return new ObservedAssistantTextDelta("hello");
            yield return new ObservedAssistantMessageCompleted();
            await Task.CompletedTask;
        }

        var committedReduceOnly = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunAsync(initial, Observed()))
            committedReduceOnly.Add(e);

        var committedWithEffects = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunWithEffectsAsync(initial, Observed(), effects: NoOpEffects.Instance))
            committedWithEffects.Add(e);

        committedWithEffects.Should().Equal(committedReduceOnly);
    }

    private sealed class NoOpEffects : IEffectExecutor
    {
        public static NoOpEffects Instance { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
    }
}
