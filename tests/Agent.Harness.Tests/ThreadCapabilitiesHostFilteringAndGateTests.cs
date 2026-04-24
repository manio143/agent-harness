using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesHostFilteringAndGateTests
{
    [Fact]
    public void FilterToolsForThread_WithDenyFsWriteAndHostExec_RemovesWritePatchAndExec()
    {
        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            ToolSchemas.ReadTextFile,
            ToolSchemas.WriteTextFile,
            ToolSchemas.PatchTextFile,
            ToolSchemas.ExecuteCommand);

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
                Allow: ImmutableArray<string>.Empty,
                Deny: ImmutableArray.Create("fs.write", "host.exec"))));

        var filtered = ThreadCapabilitiesEvaluator.FilterToolsForThread("s", "child", tools, store);

        filtered.Select(t => t.Name).Should().Contain("read_text_file");
        filtered.Select(t => t.Name).Should().NotContain("write_text_file");
        filtered.Select(t => t.Name).Should().NotContain("patch_text_file");
        filtered.Select(t => t.Name).Should().NotContain("execute_command");
    }

    [Fact]
    public async Task RouterGate_WithDenyHostExec_DeniesExecuteCommand_WithToolNotAllowed()
    {
        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            ToolSchemas.ExecuteCommand);

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
                Allow: ImmutableArray<string>.Empty,
                Deny: ImmutableArray.Create("host.exec"))));

        var state = SessionState.Empty with { Tools = tools };

        var router = new ToolCallRouter(
            new IToolCallExecutor[] { new AlwaysCompletesExecutor("execute_command") },
            gate: (s, call) => ThreadCapabilitiesEvaluator.IsToolAllowed("s", "child", call.ToolName, s.Tools, store));

        var obs = await router.ExecuteAsync(state, new ExecuteToolCall("t1", "execute_command", new { command = "ls" }), CancellationToken.None);

        obs.Should().ContainSingle();
        obs[0].Should().BeOfType<ObservedToolCallFailed>().Which.Error.Should().Be("tool_not_allowed:execute_command");
    }

    private sealed class AlwaysCompletesExecutor(string name) : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => toolName == name;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }
}
