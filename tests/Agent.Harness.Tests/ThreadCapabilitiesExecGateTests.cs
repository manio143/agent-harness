using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesExecGateTests
{
    [Fact]
    public async Task RouterGate_DeniesDisallowedToolCall_WithToolNotAllowedError()
    {
        var sessionId = "s1";
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing(sessionId);

        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: "child",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default",
            Capabilities: new ThreadCapabilitiesSpec(
                Allow: ImmutableArray<string>.Empty,
                Deny: ImmutableArray.Create("threads"))));

        var tools = ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            ToolSchemas.ThreadList);

        var state = SessionState.Empty with { Tools = tools };

        var router = new ToolCallRouter(
            new IToolCallExecutor[] { new AlwaysCompletesExecutor("thread_list") },
            gate: (s, call) => ThreadCapabilitiesEvaluator.IsToolAllowed(sessionId, "child", call.ToolName, s.Tools, store));

        var obs = await router.ExecuteAsync(state, new ExecuteToolCall("t1", "thread_list", new { }), CancellationToken.None);

        obs.Should().ContainSingle();
        obs[0].Should().BeOfType<ObservedToolCallFailed>().Which.Error.Should().Be("tool_not_allowed:thread_list");
    }

    private sealed class AlwaysCompletesExecutor(string name) : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => toolName == name;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }
}
