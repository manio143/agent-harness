using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class TurnRunnerPrefersStreamingEffectExecutorTests
{
    [Fact]
    public async Task When_streaming_executor_is_available_CallModel_is_not_executed_via_ExecuteAsync()
    {
        var effects = new SpyStreamingExecutor();

        var observed = AsyncEnumerable();

        static async IAsyncEnumerable<ObservedChatEvent> AsyncEnumerable()
        {
            // User message should cause reducer to request CallModel.
            yield return new ObservedUserMessage("hi");
            await Task.CompletedTask;
        }
        var state = SessionState.Empty;

        var committed = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunWithEffectsAsync(
            initial: state,
            observed: observed,
            effects: effects,
            sink: NullEventSink.Instance,
            options: new CoreOptions(),
            onState: null,
            cancellationToken: CancellationToken.None))
        {
            committed.Add(e);
        }

        effects.ExecuteAsyncCallModelCount.Should().Be(0);
        effects.ExecuteStreamingCallModelCount.Should().BeGreaterThan(0);
    }

    private sealed class SpyStreamingExecutor : IStreamingEffectExecutor
    {
        public int ExecuteAsyncCallModelCount { get; private set; }
        public int ExecuteStreamingCallModelCount { get; private set; }

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            if (effect is CallModel)
                ExecuteAsyncCallModelCount++;
            return Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
        }

        public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(
            SessionState state,
            Effect effect,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (effect is CallModel)
            {
                ExecuteStreamingCallModelCount++;
                yield return new ObservedAssistantTextDelta("hello");
                yield break;
            }

            await Task.CompletedTask;
            yield break;
        }
    }
}
