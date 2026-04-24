using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesSelectorTests
{
    [Fact]
    public void ExpandSelector_threads_MatchesAllThreadTools()
    {
        var tools = ImmutableArray.Create(
            new ToolDefinition("thread_list", null, default),
            new ToolDefinition("thread_read", null, default),
            new ToolDefinition("thread_send", null, default),
            new ToolDefinition("thread_start", null, default),
            new ToolDefinition("thread_stop", null, default),
            new ToolDefinition("thread_config", null, default),
            new ToolDefinition("report_intent", null, default),
            new ToolDefinition("read_text_file", null, default));

        var set = ThreadCapabilitiesEvaluator.ExpandSelectors(
            tools,
            ImmutableArray.Create("threads"));

        set.Should().BeEquivalentTo(new[]
        {
            "thread_list","thread_read","thread_send","thread_start","thread_stop","thread_config"
        });
    }

    [Fact]
    public void ExpandSelector_mcpServer_MatchesOnlyThatServerTools()
    {
        var tools = ImmutableArray.Create(
            new ToolDefinition("everything__echo", null, default),
            new ToolDefinition("everything__sum", null, default),
            new ToolDefinition("files__read", null, default),
            new ToolDefinition("thread_list", null, default));

        ThreadCapabilitiesEvaluator.ExpandSelectors(tools, ImmutableArray.Create("mcp:everything"))
            .Should().BeEquivalentTo(new[] { "everything__echo", "everything__sum" });

        ThreadCapabilitiesEvaluator.ExpandSelectors(tools, ImmutableArray.Create("mcp:*"))
            .Should().BeEquivalentTo(new[] { "everything__echo", "everything__sum", "files__read" });
    }

    [Fact]
    public void EffectiveTools_AllowThenDeny_DenyWins()
    {
        var sessionId = "s1";
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing(sessionId);

        // child inherits full, but we restrict.
        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: "child",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default",
            CompactionCount: 0,
            ClosedAtIso: null,
            ClosedReason: null,
            Capabilities: new ThreadCapabilitiesSpec(
                Allow: ImmutableArray.Create("fs.read", "fs.write"),
                Deny: ImmutableArray.Create("fs.write"))));

        var tools = ImmutableArray.Create(
            ToolSchemas.ReadTextFile,
            ToolSchemas.WriteTextFile,
            ToolSchemas.PatchTextFile,
            ToolSchemas.ExecuteCommand,
            ToolSchemas.ReportIntent,
            ToolSchemas.ThreadList);

        var effective = ThreadCapabilitiesEvaluator.FilterToolsForThread(sessionId, "child", tools, store)
            .Select(t => t.Name)
            .ToImmutableHashSet();

        effective.Should().Contain("read_text_file");
        effective.Should().NotContain("write_text_file");
        effective.Should().NotContain("patch_text_file");
        effective.Should().Contain("report_intent", "report_intent is always allowed");
    }
}
