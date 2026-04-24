using Agent.Harness.Llm.SystemPrompts;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesSystemPromptContributorTests
{
    [Fact]
    public void Build_EmitsShortCapabilitiesExplanation()
    {
        var ctx = new SystemPromptContext(
            SessionId: "s",
            SessionMetadata: null,
            ModelCatalogPrompt: null,
            ThreadId: "main",
            ThreadMetadata: null,
            OfferedToolNames: null);

        var frag = new ThreadCapabilitiesSystemPromptContributor().Build(ctx).Single();
        frag.Id.Should().Be(ThreadCapabilitiesSystemPromptContributor.FragmentId);
        frag.Content.Should().Contain("capabilities", because: "should explain per-thread tool surface");
        frag.Content.Should().Contain("Only call tools", because: "should instruct using tool catalog");
    }
}
