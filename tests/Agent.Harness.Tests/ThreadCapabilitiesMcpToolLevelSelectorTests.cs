using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesMcpToolLevelSelectorTests
{
    [Fact]
    public void FilterToolsForThread_WithAllowSpecificMcpTool_OffersOnlyThatTool()
    {
        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            new ToolDefinition("files__read", "", JsonDocument.Parse("{}").RootElement),
            new ToolDefinition("files__write", "", JsonDocument.Parse("{}").RootElement),
            new ToolDefinition("everything__echo", "", JsonDocument.Parse("{}").RootElement));

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
                Allow: ImmutableArray.Create("mcp:files:read"),
                Deny: ImmutableArray<string>.Empty)));

        var filtered = ThreadCapabilitiesEvaluator.FilterToolsForThread("s", "child", tools, store);
        var names = filtered.Select(t => t.Name).ToImmutableHashSet();

        names.Should().Contain("files__read");
        names.Should().NotContain("files__write");
        names.Should().NotContain("everything__echo");
        names.Should().Contain("report_intent");
    }

    [Fact]
    public async Task RouterGate_WithAllowSpecificMcpTool_DeniesOtherMcpTools()
    {
        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            new ToolDefinition("files__read", "", JsonDocument.Parse("{}").RootElement),
            new ToolDefinition("files__write", "", JsonDocument.Parse("{}").RootElement));

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
                Allow: ImmutableArray.Create("mcp:files:read"),
                Deny: ImmutableArray<string>.Empty)));

        var state = SessionState.Empty with { Tools = tools };

        var router = new ToolCallRouter(
            new IToolCallExecutor[]
            {
                new AlwaysCompletesExecutor("files__read"),
                new AlwaysCompletesExecutor("files__write"),
            },
            gate: (s, call) => ThreadCapabilitiesEvaluator.IsToolAllowed("s", "child", call.ToolName, s.Tools, store));

        var obs = await router.ExecuteAsync(state, new ExecuteToolCall("t1", "files__write", new { }), CancellationToken.None);

        obs.Should().ContainSingle();
        obs[0].Should().BeOfType<ObservedToolCallFailed>().Which.Error.Should().Be("tool_not_allowed:files__write");
    }

    private sealed class AlwaysCompletesExecutor(string name) : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => toolName == name;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }
}
