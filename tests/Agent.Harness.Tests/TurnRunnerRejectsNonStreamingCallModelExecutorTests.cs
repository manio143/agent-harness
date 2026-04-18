using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class TurnRunnerRejectsNonStreamingCallModelExecutorTests
{
    [Fact]
    public async Task CallModel_requires_streaming_effect_executor()
    {
        var effects = new NonStreamingExecutor();

        static async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("hi");
            await Task.CompletedTask;
        }

        Func<Task> act = async () =>
        {
            await foreach (var _ in TurnRunner.RunWithEffectsAsync(
                initial: SessionState.Empty,
                observed: Observed(),
                effects: effects,
                sink: NullEventSink.Instance,
                options: new CoreOptions(),
                onState: null,
                cancellationToken: CancellationToken.None))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("call_model_requires_streaming_effect_executor");
    }

    private sealed class NonStreamingExecutor : IEffectExecutor
    {
        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
    }
}
