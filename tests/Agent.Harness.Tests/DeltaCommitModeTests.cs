using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class DeltaCommitModeTests
{
    [Fact]
    public void WhenCommitAssistantTextDeltasEnabled_DeltaIsCommittedImmediately()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(
            state,
            new ObservedAssistantTextDelta("Hel"),
            new CoreOptions(CommitAssistantTextDeltas: true));

        result.NewlyCommitted.Should().ContainSingle();
        result.NewlyCommitted[0].Should().Be(new AssistantTextDelta("Hel"));
    }

    [Fact]
    public async Task TurnRunner_InDeltaMode_YieldsDeltaEventsBeforeCompletion_AndStillCommitsFinalMessage()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Hello");
            yield return new ObservedAssistantTextDelta("Hel");
            await Task.Yield();
            yield return new ObservedAssistantTextDelta("lo");
            yield return new ObservedAssistantMessageCompleted();
        }

        var committed = new List<SessionEvent>();

        await foreach (var e in TurnRunner.RunWithEffectsAsync(
            SessionState.Empty,
            Observed(),
            effects: NullEffectExecutor.Instance,
            options: new CoreOptions(CommitAssistantTextDeltas: true)))
        {
            committed.Add(e);
        }

        committed.Should().Equal(
            new UserMessage("Hello"),
            new AssistantTextDelta("Hel"),
            new AssistantTextDelta("lo"),
            new AssistantMessage("Hello"),
            new TurnEnded());
    }
}
