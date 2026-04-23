using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MeaiPromptRendererCompactionTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Render_WhenCompactionCommitted_PrependsSummarySystemMessage_AndTrimsToTailMessages()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new UserMessage("u1"),
                new AssistantMessage("a1"),
                new CompactionCommitted(J(new { k = 1 }), "SUMMARY"),
                new UserMessage("u2"),
                new AssistantMessage("a2"),
                new UserMessage("u3"),
                new AssistantMessage("a3"),
                new UserMessage("u4"),
                new AssistantMessage("a4"))
        };

        var msgs = MeaiPromptRenderer.Render(state, compactionTailMessageCount: 4);

        // Summary system message is first (after always-injected system prompts, which are added later).
        msgs[0].Role.ToString().Should().Be("system");
        msgs[0].Text.Should().Contain("SUMMARY");

        // Tail should include only the last 4 user/assistant messages: u3,a3,u4,a4.
        var tailTexts = msgs.Skip(1).Select(m => m.Text).ToList();
        tailTexts.Should().NotContain("u2");
        tailTexts.Should().Contain(new[] { "u3", "a3", "u4", "a4" });
    }

    [Fact]
    public void Render_WhenToolResultsExistAfterLastAssistant_IncludesToolCallsAndResultsInTail()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new UserMessage("before"),
                new AssistantMessage("planning"),
                new ToolCallRequested("call_1", "read_text_file", J(new { path = "/tmp/a.txt" })),
                new ToolCallCompleted("call_1", J(new { body = "VERY_BIG_SHOULD_NOT_MATTER" })),
                new CompactionCommitted(J(new { s = 1 }), "SUMMARY"))
        };

        var msgs = MeaiPromptRenderer.Render(state, compactionTailMessageCount: 1);

        // Expect the function call + function result to be present even though tail message count is tiny.
        msgs.Any(m => m.Contents != null && m.Contents.OfType<FunctionCallContent>().Any(fc => fc.CallId == "call_1"))
            .Should().BeTrue();
        msgs.Any(m => m.Contents != null && m.Contents.OfType<FunctionResultContent>().Any(fr => fr.CallId == "call_1"))
            .Should().BeTrue();
    }
}
