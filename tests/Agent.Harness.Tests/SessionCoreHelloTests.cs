using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerHelloTests
{
    [Fact]
    public void GivenEmptyState_WhenObservedUserMessage_ThenCommitsUserMessageEvent()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedUserMessage("Hello"));

        result.NewlyCommitted.Should().ContainSingle();
        result.NewlyCommitted[0].Should().Be(new UserMessageAdded("Hello"));

        result.Next.Committed.Should().Contain(new UserMessageAdded("Hello"));
    }

    [Fact]
    public void GivenBufferedAssistantText_WhenCompleted_ThenCommitsAssistantMessage()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty,
            Buffer: new TurnBuffer(AssistantText: "Hello back", AssistantMessageOpen: true));

        var result = Core.Reduce(state, new ObservedAssistantMessageCompleted());

        result.NewlyCommitted.Should().ContainSingle(e => e is AssistantMessageAdded);
        result.Next.Buffer.AssistantMessageOpen.Should().BeFalse();
        result.Next.Buffer.AssistantText.Should().BeEmpty();
    }

    [Fact]
    public void RenderPrompt_ContainsCommittedUserAndAssistantMessages_InOrder()
    {
        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessageAdded("Hello"),
                new AssistantMessageAdded("Hello back")),
            Buffer: TurnBuffer.Empty);

        var rendered = Core.RenderPrompt(state);

        rendered.Should().Equal(
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "Hello back"));
    }
}
