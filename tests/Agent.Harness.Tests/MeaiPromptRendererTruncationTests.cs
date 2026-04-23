using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Llm;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class MeaiPromptRendererTruncationTests
{
    [Fact]
    public void Render_WhenMaxTailCharsSet_TruncatesOversizedUserMessage()
    {
        var huge = new string('x', 50);

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new CompactionCommitted(System.Text.Json.JsonSerializer.SerializeToElement(new { s = 1 }), "sum"),
                new UserMessage(huge))
        };

        var msgs = MeaiPromptRenderer.Render(
            state,
            compactionTailMessageCount: 1,
            maxTailMessageChars: 10);

        msgs.Should().ContainSingle(m => m.Role.ToString() == "user");
        var user = msgs.Single(m => m.Role.ToString() == "user");
        user.Text.Length.Should().BeLessThanOrEqualTo(100);
        user.Text.Should().Contain("TRUNCATED");
    }
}
