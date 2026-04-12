using System.Collections.Immutable;
using Agent.Harness.TitleGeneration;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SessionRunnerTitleIntegrationTests
{
    [Fact]
    public async Task RunTurn_commits_title_event_after_first_turn()
    {
        var coreOptions = new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false);
        var chat = new ScriptedChatClient().WhenCalledReturn("My Title");
        var titleGen = new SessionTitleGenerator(chat);
        var runner = new SessionRunner(coreOptions, titleGen);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Hi");
            yield return new ObservedAssistantTextDelta("Hello");
            yield return new ObservedAssistantMessageCompleted(null);
            await Task.Yield();
        }

        var result = await runner.RunTurnAsync(SessionState.Empty, Observed(), CancellationToken.None);

        result.NewlyCommitted.Should().ContainInOrder(
            new UserMessage("Hi"),
            new AssistantMessage("Hello"),
            new SessionTitleSet("My Title"));

        result.Next.Committed.Should().Contain(new SessionTitleSet("My Title"));
    }

    [Fact]
    public async Task RunTurn_does_not_generate_title_if_already_present()
    {
        var coreOptions = new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false);
        var chat = new ScriptedChatClient().WhenCalledReturn("SHOULD_NOT_BE_USED");
        var titleGen = new SessionTitleGenerator(chat);
        var runner = new SessionRunner(coreOptions, titleGen);

        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Hi"),
                new AssistantMessage("Hello"),
                new SessionTitleSet("Existing")),
            Buffer: TurnBuffer.Empty);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Next");
            yield return new ObservedAssistantTextDelta("Ok");
            yield return new ObservedAssistantMessageCompleted(null);
            await Task.Yield();
        }

        var result = await runner.RunTurnAsync(initial, Observed(), CancellationToken.None);

        result.NewlyCommitted.OfType<SessionTitleSet>().Should().NotContain(st => st.Title == "SHOULD_NOT_BE_USED");
        chat.Calls.Should().BeEmpty();
    }
}
