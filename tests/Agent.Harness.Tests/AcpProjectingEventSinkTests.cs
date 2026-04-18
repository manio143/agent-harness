using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpProjectingEventSinkTests
{
    [Fact]
    public async Task WhenLaterEffectThrows_EarlierProjectedEmissionsWereExecuted()
    {
        var core = new CoreOptions { CommitAssistantTextDeltas = true, CommitReasoningTextDeltas = false };
        var publish = new AcpPublishOptions { PublishReasoning = false };

        var state = SessionState.Empty;

        // First observation commits an assistant delta (projects to an agent message chunk).
        // Second observation commits a user message (no projection) and emits CallModel; our effect executor throws.
        var observed = AsyncEnumerableOf(
            new ObservedAssistantTextDelta("hi"),
            new ObservedUserMessage("boom"));

        var executed = new List<AcpEmission>();

        var sink = new AcpProjectingEventSink(
            inner: NullEventSink.Instance,
            coreOptions: core,
            publishOptions: publish,
            execute: (e, _) =>
            {
                executed.Add(e);
                return ValueTask.CompletedTask;
            });

        var effects = new ThrowOnCallModel();

        Func<Task> act = async () =>
        {
            await foreach (var _ in TurnRunner.RunWithEffectsAsync(state, observed, effects, sink: sink, options: core))
            {
                // consume
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        executed.Should().Contain(new AcpSendAgentMessageChunk("hi"));
    }

    private static async IAsyncEnumerable<ObservedChatEvent> AsyncEnumerableOf(params ObservedChatEvent[] items)
    {
        foreach (var i in items)
            yield return i;

        await Task.CompletedTask;
    }

    private sealed class ThrowOnCallModel : IStreamingEffectExecutor
    {
        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);

        public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (effect is CallModel)
                throw new InvalidOperationException("boom");

            yield break;
        }
    }
}
