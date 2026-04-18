using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class EventSinkOrderingTests
{
    [Fact]
    public async Task RunWithEffectsAsync_SinksCommittedEvents_BeforeExecutingEffects()
    {
        var state = SessionState.Empty;

        var observed = AsyncEnumerableOf(new ObservedUserMessage("hi"));

        var sink = new RecordingSink();
        var effects = new AssertingEffectExecutor(sink);

        // enumerate until the effect executor runs (it will assert ordering)
        var committed = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunWithEffectsAsync(state, observed, effects, sink: sink))
            committed.Add(e);

        sink.Committed.Should().ContainSingle(e => e is UserMessage);
    }

    [Fact]
    public async Task RunWithEffectsAsync_WhenEffectThrows_CommittedEventsWereAlreadySunk()
    {
        var state = SessionState.Empty;

        var observed = AsyncEnumerableOf(new ObservedUserMessage("hi"));

        var sink = new RecordingSink();
        var effects = new ThrowingEffectExecutor();

        Func<Task> act = async () =>
        {
            await foreach (var _ in TurnRunner.RunWithEffectsAsync(state, observed, effects, sink: sink))
            {
                // consume
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
        sink.Committed.Should().ContainSingle(e => e is UserMessage);
    }

    private static async IAsyncEnumerable<ObservedChatEvent> AsyncEnumerableOf(params ObservedChatEvent[] items)
    {
        foreach (var i in items)
            yield return i;

        await Task.CompletedTask;
    }

    private sealed class RecordingSink : IEventSink
    {
        public List<ObservedChatEvent> Observed { get; } = new();
        public List<SessionEvent> Committed { get; } = new();

        public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default)
        {
            Observed.Add(observed);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default)
        {
            Committed.Add(committed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AssertingEffectExecutor : IStreamingEffectExecutor
    {
        private readonly RecordingSink _sink;

        public AssertingEffectExecutor(RecordingSink sink)
        {
            _sink = sink;
        }

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);

        public IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            // The first effect after a user message is expected to be CallModel.
            effect.Should().BeOfType<CallModel>();

            // Invariant under test: user message commit is sunk before any effects execute.
            _sink.Committed.Should().Contain(e => e is UserMessage);

            return Empty();

            static async IAsyncEnumerable<ObservedChatEvent> Empty()
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }

    private sealed class ThrowingEffectExecutor : IStreamingEffectExecutor
    {
        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");

        public IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }
}
