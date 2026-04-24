using System.Collections.Immutable;
using Agent.Harness.Llm.SystemPrompts;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadingGuidanceSystemPromptContributorGatingTests
{
    [Fact]
    public void Build_WhenThreadToolsAreNotOffered_IsEmpty()
    {
        var ctx = new SystemPromptContext(
            SessionId: "s",
            SessionMetadata: null,
            ModelCatalogPrompt: null,
            ThreadId: "child",
            ThreadMetadata: new ThreadMetadata(
                ThreadId: "child",
                ParentThreadId: ThreadIds.Main,
                Intent: null,
                CreatedAtIso: "t0",
                UpdatedAtIso: "t0",
                Mode: ThreadMode.Multi,
                Model: "default",
                Capabilities: null),
            OfferedToolNames: ImmutableHashSet.Create<string>(StringComparer.Ordinal, "report_intent", "read_text_file"));

        new ThreadingGuidanceSystemPromptContributor().Build(ctx).Should().BeEmpty();
    }

    [Fact]
    public void Build_WhenThreadToolsAreOffered_IncludesFragment()
    {
        var ctx = new SystemPromptContext(
            SessionId: "s",
            SessionMetadata: null,
            ModelCatalogPrompt: null,
            ThreadId: "main",
            ThreadMetadata: null,
            OfferedToolNames: ImmutableHashSet.Create<string>(StringComparer.Ordinal, "thread_start", "thread_list"));

        new ThreadingGuidanceSystemPromptContributor().Build(ctx)
            .Single().Id.Should().Be(ThreadingGuidanceSystemPromptContributor.FragmentId);
    }
}
