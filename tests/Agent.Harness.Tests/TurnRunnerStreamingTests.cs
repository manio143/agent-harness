using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class TurnRunnerStreamingTests
{
    [Fact]
    public async Task RunObservedStream_YieldsOnlyCommittedEvents_DeltasAreWithheldUntilCompletion()
    {
        var observed = GetObserved();

        var committed = new List<SessionEvent>();

        await foreach (var e in TurnRunner.RunAsync(SessionState.Empty, observed))
        {
            committed.Add(e);
        }

        committed.Should().Equal(
            new UserMessage("Hello"),
            new AssistantMessage("Hello back"),
            new TurnEnded());

        return;

        static async IAsyncEnumerable<ObservedChatEvent> GetObserved()
        {
            yield return new ObservedUserMessage("Hello");
            yield return new ObservedAssistantTextDelta("Hello");
            yield return new ObservedAssistantTextDelta(" back");

            // No committed assistant message until boundary.
            await Task.Yield();

            yield return new ObservedAssistantMessageCompleted("stop");
        }
    }

    [Fact]
    public async Task RunObservedStream_ExposesFinalStateSnapshot()
    {
        var observed = GetObserved();
        SessionState? final = null;

        await foreach (var _ in TurnRunner.RunAsync(SessionState.Empty, observed, onState: s => final = s))
        {
            // ignore
        }

        final.Should().NotBeNull();
        final!.Committed.Should().Equal(
            ImmutableArray.Create<SessionEvent>(
                new UserMessage("Hello"),
                new AssistantMessage("Hello back"),
                new TurnEnded()));

        return;

        static async IAsyncEnumerable<ObservedChatEvent> GetObserved()
        {
            yield return new ObservedUserMessage("Hello");
            yield return new ObservedAssistantTextDelta("Hello back");
            await Task.Yield();
            yield return new ObservedAssistantMessageCompleted();
        }
    }
}
