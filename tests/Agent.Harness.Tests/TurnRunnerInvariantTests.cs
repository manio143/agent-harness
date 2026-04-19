using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class TurnRunnerInvariantTests
{
    [Fact]
    public async Task RunWithEffectsAsync_EndsTurnExactlyOnce_AndNeverCommitsAfterTurnEnded()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            // Minimal observed stream that commits something.
            yield return new ObservedTurnStarted(ThreadIds.Main);
            yield return new ObservedAssistantTextDelta("hello");
            yield return new ObservedAssistantMessageCompleted();
            await Task.CompletedTask;
        }

        var committed = new List<SessionEvent>();

        await foreach (var e in TurnRunner.RunWithEffectsAsync(
            SessionState.Empty,
            Observed(),
            effects: NullEffectExecutor.Instance))
        {
            committed.Add(e);
        }

        committed.OfType<TurnEnded>().Should().HaveCount(1);
        committed.Last().Should().BeOfType<TurnEnded>();

        // Sanity: the committed output includes the message we reduced.
        committed.OfType<AssistantMessage>().Single(m => m.Text == "hello").Should().NotBeNull();
    }

    [Fact]
    public void Stabilization_WithPendingEnqueueInbox_DoesNotCommitTurnEnded_AndRequestsCallModel()
    {
        // Arrange: pending enqueued message exists.
        var state = SessionState.Empty with
        {
            Buffer = TurnBuffer.Empty,
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new ThreadInboxMessageEnqueued(
                    ThreadId: ThreadIds.Main,
                    EnvelopeId: "env_1",
                    Kind: ThreadInboxMessageKind.InterThreadMessage,
                    Meta: null,
                    Source: "thread",
                    SourceThreadId: "thr_x",
                    Delivery: "enqueue",
                    EnqueuedAtIso: "t0",
                    Text: "hello"))
        };

        var result = Core.Reduce(state, new ObservedTurnStabilized(ThreadIds.Main));

        result.NewlyCommitted.OfType<ThreadInboxMessageDequeued>().Should().ContainSingle(d => d.EnvelopeId == "env_1");
        result.NewlyCommitted.OfType<InterThreadMessage>().Should().ContainSingle(m => m.Text == "hello");
        result.NewlyCommitted.OfType<TurnEnded>().Should().BeEmpty();
        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }
}
