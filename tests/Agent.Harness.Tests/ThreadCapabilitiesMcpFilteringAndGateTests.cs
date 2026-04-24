using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesMcpFilteringAndGateTests
{
    [Fact]
    public void FilterToolsForThread_WithAllowMcpStarDenyFiles_RemovesFilesServerTools()
    {
        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            new ToolDefinition("everything__echo", "", JsonDocument.Parse("{}").RootElement),
            new ToolDefinition("files__read", "", JsonDocument.Parse("{}").RootElement));

        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing("s");
        store.CreateThread("s", new ThreadMetadata(
            ThreadId: "child",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default",
            Capabilities: new ThreadCapabilitiesSpec(
                Allow: ImmutableArray.Create("mcp:*"),
                Deny: ImmutableArray.Create("mcp:files"))));

        var filtered = ThreadCapabilitiesEvaluator.FilterToolsForThread("s", "child", tools, store);

        filtered.Select(t => t.Name).Should().Contain("everything__echo");
        filtered.Select(t => t.Name).Should().NotContain("files__read");
        filtered.Select(t => t.Name).Should().Contain("report_intent");
    }

    [Fact]
    public async Task RouterGate_WithDenyFilesServer_DeniesFilesMcpToolCall_WithToolNotAllowed()
    {
        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            new ToolDefinition("everything__echo", "", JsonDocument.Parse("{}").RootElement),
            new ToolDefinition("files__read", "", JsonDocument.Parse("{}").RootElement));

        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing("s");
        store.CreateThread("s", new ThreadMetadata(
            ThreadId: "child",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default",
            Capabilities: new ThreadCapabilitiesSpec(
                Allow: ImmutableArray.Create("mcp:*"),
                Deny: ImmutableArray.Create("mcp:files"))));

        var state = SessionState.Empty with { Tools = tools };

        var router = new ToolCallRouter(
            new IToolCallExecutor[]
            {
                new AlwaysCompletesExecutor("everything__echo"),
                new AlwaysCompletesExecutor("files__read"),
            },
            gate: (s, call) => ThreadCapabilitiesEvaluator.IsToolAllowed("s", "child", call.ToolName, s.Tools, store));

        var obs = await router.ExecuteAsync(state, new ExecuteToolCall("t1", "files__read", new { path = "x" }), CancellationToken.None);

        obs.Should().ContainSingle();
        obs[0].Should().BeOfType<ObservedToolCallFailed>().Which.Error.Should().Be("tool_not_allowed:files__read");
    }

    private sealed class AlwaysCompletesExecutor(string name) : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => toolName == name;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }
}
