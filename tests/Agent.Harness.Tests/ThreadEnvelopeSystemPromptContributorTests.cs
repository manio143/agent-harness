using Agent.Harness.Llm.SystemPrompts;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadEnvelopeSystemPromptContributorTests
{
    [Fact]
    public void Build_IncludesCompactionCount()
    {
        var meta = new ThreadMetadata(
            ThreadId: "main",
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: "2026-01-01T00:00:00Z",
            UpdatedAtIso: "2026-01-01T00:00:00Z",
            Mode: ThreadMode.Multi,
            Model: null,
            CompactionCount: 2);

        var ctx = new SystemPromptContext(
            SessionId: "sid",
            SessionMetadata: null,
            ModelCatalogPrompt: null,
            ThreadId: "main",
            ThreadMetadata: meta);

        var frag = new ThreadEnvelopeSystemPromptContributor().Build(ctx).Single();

        frag.Content.Should().Contain("\"compactionCount\":2");
        frag.Content.Should().Contain("\"mode\":\"multi\"");
    }
}
