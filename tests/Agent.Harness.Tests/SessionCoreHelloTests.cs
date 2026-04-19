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
        result.NewlyCommitted[0].Should().Be(new UserMessage("Hello"));

        result.Next.Committed.Should().Contain(new UserMessage("Hello"));
    }

    [Fact]
    public void GivenBufferedAssistantText_WhenCompleted_ThenCommitsAssistantMessage()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty,
            Buffer: new TurnBuffer(AssistantText: "Hello back", AssistantMessageOpen: true, ReasoningText: "", ReasoningMessageOpen: false, IntentReportedThisTurn: false, TurnStartedFromIdle: false),
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var result = Core.Reduce(state, new ObservedAssistantMessageCompleted());

        result.NewlyCommitted.Should().ContainSingle(e => e is AssistantMessage);
        result.Next.Buffer.AssistantMessageOpen.Should().BeFalse();
        result.Next.Buffer.AssistantText.Should().BeEmpty();
    }

    [Fact]
    public void RenderPrompt_ContainsCommittedUserAndAssistantMessages_InOrder()
    {
        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Hello"),
                new AssistantMessage("Hello back")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var rendered = Core.RenderPrompt(state);

        rendered.Should().Equal(
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "Hello back"));
    }

    [Fact]
    public void RenderPrompt_IncludesToolCallsAndOutcomes_AsToolMessages()
    {
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "/x", content = "hi" });
        var result = System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true });

        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("do it"),
                new ToolCallRequested("call_1", "write_text_file", args),
                new ToolCallCompleted("call_1", result),
                new AssistantMessage("done")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var rendered = Core.RenderPrompt(state);

        rendered[0].Should().Be(new ChatMessage(ChatRole.User, "do it"));
        rendered[1].Role.Should().Be(ChatRole.System);
        rendered[1].Text.Should().Contain("<tool_call>");
        rendered[1].Text.Should().Contain("\"toolId\":\"call_1\"");
        rendered[1].Text.Should().Contain("\"toolName\":\"write_text_file\"");

        rendered[2].Role.Should().Be(ChatRole.Tool);
        rendered[2].Text.Should().Contain("\"outcome\":\"completed\"");

        rendered[3].Should().Be(new ChatMessage(ChatRole.Assistant, "done"));
    }
}
