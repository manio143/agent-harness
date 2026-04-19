using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolExecutorCoverageTests
{
    private sealed class CapturingThreadTools : Agent.Harness.Threads.IThreadTools
    {
        public string? LastIntent { get; private set; }

        public void ReportIntent(string threadId, string intent) => LastIntent = intent;

        public ImmutableArray<Agent.Harness.Threads.ThreadInfo> Threads { get; init; } = ImmutableArray<Agent.Harness.Threads.ThreadInfo>.Empty;

        public ImmutableArray<Agent.Harness.Threads.ThreadMessage> Messages { get; init; } = ImmutableArray<Agent.Harness.Threads.ThreadMessage>.Empty;

        public ImmutableArray<Agent.Harness.Threads.ThreadInfo> List() => Threads;

        public ImmutableArray<Agent.Harness.Threads.ThreadMessage> ReadThreadMessages(string threadId) => Messages;

        public string GetModel(string threadId) => "default";
    }

    private sealed class CapturingObserver : Agent.Harness.Threads.IThreadObserver
    {
        public Task ObserveAsync(string threadId, ObservedChatEvent observed, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CapturingLifecycle : Agent.Harness.Threads.IThreadLifecycle
    {
        public Task RequestForkChildThreadAsync(string parentThreadId, string childThreadId, ImmutableArray<SessionEvent> seed, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RequestSetThreadModelAsync(string threadId, string model, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CapturingScheduler : Agent.Harness.Threads.IThreadScheduler
    {
        public void ScheduleRun(string threadId) { }
    }

    private sealed class AlwaysExecutor(string name) : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => toolName == name;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }

    [Fact]
    public void ToolArgs_Normalize_WhenJsonElementObject_ReturnsCaseInsensitiveDictionary()
    {
        var je = JsonDocument.Parse("{\"Path\":\"x\"}").RootElement.Clone();
        var dict = ToolArgs.Normalize(je);

        dict.Should().ContainKey("path");
        dict["path"].GetString().Should().Be("x");
    }

    [Fact]
    public void ToolArgs_Normalize_WhenNotObject_ReturnsEmpty()
    {
        var je = JsonDocument.Parse("[1,2]").RootElement.Clone();
        var dict = ToolArgs.Normalize(je);

        dict.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallRouter_WhenUnknownTool_ReturnsObservedToolCallFailed_unknown_tool()
    {
        var router = new ToolCallRouter(Array.Empty<IToolCallExecutor>());

        var obs = await router.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "nope", new { }),
            CancellationToken.None);

        obs.Should().HaveCount(1);
        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("unknown_tool");
    }

    [Fact]
    public async Task SystemToolCallExecutor_thread_send_CrossThread_RequiresOrchestrator()
    {
        var exec = new SystemToolCallExecutor(
            threadTools: null,
            observer: null,
            lifecycle: null,
            scheduler: null,
            isKnownModel: null,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_send", new { threadId = "thr_other", message = "hi" }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_tools_require_orchestrator");
    }

    [Fact]
    public async Task SystemToolCallExecutor_report_intent_ReportsIntent_WhenThreadToolsProvided()
    {
        var tools = new CapturingThreadTools();
        var exec = new SystemToolCallExecutor(
            threadTools: tools,
            observer: null,
            lifecycle: null,
            scheduler: null,
            isKnownModel: null,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "report_intent", new { intent = "do stuff" }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single();
        tools.LastIntent.Should().Be("do stuff");
    }

    [Fact]
    public async Task SystemToolCallExecutor_thread_start_InvalidContext_IsFailed_WhenOrchestratorPresent()
    {
        var exec = new SystemToolCallExecutor(
            threadTools: new CapturingThreadTools(),
            observer: new CapturingObserver(),
            lifecycle: new CapturingLifecycle(),
            scheduler: new CapturingScheduler(),
            isKnownModel: null,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_start", new { context = "bad", message = "hi" }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_start.invalid_context");
    }

    [Fact]
    public async Task SystemToolCallExecutor_thread_config_UnknownModel_IsFailed()
    {
        var exec = new SystemToolCallExecutor(
            threadTools: null,
            observer: null,
            lifecycle: null,
            scheduler: null,
            isKnownModel: m => m == "known",
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_config", new { model = "unknown" }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_config.unknown_model");
    }

    [Fact]
    public async Task SystemToolCallExecutor_thread_list_and_thread_read_UsesThreadTools()
    {
        var tools = new CapturingThreadTools
        {
            Threads = ImmutableArray.Create(new Agent.Harness.Threads.ThreadInfo("thr_1", ParentThreadId: null, Status: Agent.Harness.Threads.ThreadStatus.Idle, Intent: null, Model: "default")),
            Messages = ImmutableArray.Create(new Agent.Harness.Threads.ThreadMessage(Role: "assistant", Text: "hi")),
        };

        var exec = new SystemToolCallExecutor(
            threadTools: tools,
            observer: null,
            lifecycle: null,
            scheduler: null,
            isKnownModel: null,
            threadId: "thr_main");

        (await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_list", new { }), CancellationToken.None))
            .OfType<ObservedToolCallCompleted>().Single();

        (await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t2", "thread_read", new { threadId = "thr_1" }), CancellationToken.None))
            .OfType<ObservedToolCallCompleted>().Single();
    }

    [Fact]
    public async Task SystemToolCallExecutor_thread_send_ToSelf_ReturnsInboxArrival()
    {
        var exec = new SystemToolCallExecutor(
            threadTools: null,
            observer: null,
            lifecycle: null,
            scheduler: null,
            isKnownModel: null,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_send", new { threadId = "thr_main", message = "hi" }),
            CancellationToken.None);

        obs.OfType<ObservedInboxMessageArrived>().Single();
        obs.OfType<ObservedToolCallCompleted>().Single();
    }

    [Fact]
    public async Task ToolCallRouter_WhenExecutorMatches_UsesThatExecutor()
    {
        var router = new ToolCallRouter(new IToolCallExecutor[] { new AlwaysExecutor("x") });

        var obs = await router.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "x", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single().ToolId.Should().Be("t1");
    }

    [Fact]
    public async Task ToolCallRouter_WhenFirstDoesNotMatch_UsesLaterExecutor()
    {
        var router = new ToolCallRouter(new IToolCallExecutor[] { new AlwaysExecutor("no"), new AlwaysExecutor("yes") });

        var obs = await router.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "yes", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single().ToolId.Should().Be("t1");
    }
}

