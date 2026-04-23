using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Tools.Executors;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SystemToolCallExecutorMoreCoverageTests
{
    [Fact]
    public async Task ThreadConfig_WhenModelEmpty_FailsWithModelRequired()
    {
        var exec = new SystemToolCallExecutor(threadTools: null, observer: null, lifecycle: null, scheduler: null, isKnownModel: _ => true, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_config", new { model = "" }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_config.model_required");
    }

    [Fact]
    public async Task ThreadConfig_WhenCrossThreadWriteWithoutLifecycle_FailsRequiresOrchestrator()
    {
        var threads = new FakeThreadTools(model: "default");
        var exec = new SystemToolCallExecutor(threadTools: threads, observer: null, lifecycle: null, scheduler: null, isKnownModel: _ => true, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_config", new { threadId = "thr_other", model = "x" }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_tools_require_orchestrator");
    }

    [Fact]
    public async Task ThreadStart_WhenOrchestratorMissing_FailsRequiresOrchestrator()
    {
        var exec = new SystemToolCallExecutor(threadTools: null, observer: null, lifecycle: null, scheduler: null, isKnownModel: _ => true, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_start", new { name = "child", context = "new", mode = "multi", message = "hi" }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_tools_require_orchestrator");
    }

    [Fact]
    public async Task ThreadStart_WhenModelEmpty_FailsWithModelRequired()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(threadTools: tools, observer: observer, lifecycle: lifecycle, scheduler: scheduler, threadIdAllocator: new TestThreadIdAllocator("0000"), isKnownModel: _ => true, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_start", new { name = "child", context = "new", mode = "multi", message = "hi", model = "" }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_start.model_required");
        lifecycle.Forked.Should().BeTrue();
    }

    [Fact]
    public async Task ThreadStart_WhenUnknownModel_FallsBackToDefault()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(threadTools: tools, observer: observer, lifecycle: lifecycle, scheduler: scheduler, threadIdAllocator: new TestThreadIdAllocator("0000"), isKnownModel: _ => false, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_start", new { name = "child", context = "new", mode = "multi", message = "hi", model = "bad" }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Should().BeEmpty();
        obs.OfType<ObservedToolCallCompleted>().Single();

        observer.Observed.Should().HaveCount(2);
        observer.Observed[0].threadId.Should().Be("child-0000");
        observer.Observed[0].observed.Should().BeOfType<ObservedSetModel>().Which.Model.Should().Be("default");

        observer.Observed[1].threadId.Should().Be("child-0000");
        observer.Observed[1].observed.Should().BeOfType<ObservedInboxMessageArrived>();
    }

    [Fact]
    public async Task ThreadStart_WhenSuccessful_EnqueuesNewThreadTask_InChildThread()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(threadTools: tools, observer: observer, lifecycle: lifecycle, scheduler: scheduler, threadIdAllocator: new TestThreadIdAllocator("0000"), isKnownModel: _ => true, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_start", new { name = "child", context = "new", mode = "multi", message = "hi", delivery = "immediate" }), CancellationToken.None);

        var completed = obs.OfType<ObservedToolCallCompleted>().Single();
        var json = (JsonElement)completed.Result;
        var childId = json.GetProperty("threadId").GetString();
        childId.Should().NotBeNullOrWhiteSpace();

        lifecycle.Forked.Should().BeTrue();
        scheduler.Scheduled.Should().ContainSingle().Which.Should().Be(childId);

        observer.Observed.Should().ContainSingle();
        observer.Observed[0].threadId.Should().Be(childId);

        var arrived = observer.Observed[0].observed.Should().BeOfType<ObservedInboxMessageArrived>().Subject;
        arrived.Kind.Should().Be(ThreadInboxMessageKind.NewThreadTask);
        arrived.Text.Should().Be("hi");
        arrived.Meta.Should().NotBeNull();
        arrived.Meta!.Should().ContainKey(ThreadInboxMetaKeys.ParentThreadId).WhoseValue.Should().Be("thr_main");
        arrived.Meta!.Should().ContainKey(ThreadInboxMetaKeys.IsFork).WhoseValue.Should().Be("false");
    }

    [Fact]
    public async Task ThreadStart_WhenContextFork_DoesNotSeedPriorNewThreadTaskMarkers()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(threadTools: tools, observer: observer, lifecycle: lifecycle, scheduler: scheduler, threadIdAllocator: new TestThreadIdAllocator("0000"), isKnownModel: _ => true, threadId: "thr_parent");

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new UserMessage("history"),
                new NewThreadTask(ThreadId: "thr_parent", ParentThreadId: "main", IsFork: false, Message: "bootstrap"))
        };

        var obs = await exec.ExecuteAsync(state, new ExecuteToolCall("t1", "thread_start", new { name = "child", context = "fork", mode = "multi", message = "child task" }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single();

        lifecycle.Seed.Should().ContainSingle(e => e is UserMessage);
        lifecycle.Seed.Should().NotContain(e => e is NewThreadTask);
    }

    [Fact]
    public async Task ThreadSend_WhenDeliveryUnknownString_FallsBackToImmediate()
    {
        var exec = new SystemToolCallExecutor(threadTools: null, observer: null, lifecycle: null, scheduler: null, isKnownModel: null, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_send", new { threadId = "thr_main", message = "hi", delivery = "weird" }), CancellationToken.None);

        var arrived = obs.OfType<ObservedInboxMessageArrived>().Single();
        arrived.Delivery.Should().Be(InboxDelivery.Immediate);
    }

    [Fact]
    public async Task ThreadSend_WhenCrossThreadImmediate_SchedulesRun()
    {
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools("default")
        {
            Meta = new ThreadMetadata(
                ThreadId: "thr_other",
                ParentThreadId: null,
                Intent: null,
                CreatedAtIso: "t0",
                UpdatedAtIso: "t0",
                Mode: ThreadMode.Multi,
                Model: "default")
        };

        var exec = new SystemToolCallExecutor(threadTools: tools, observer: observer, lifecycle: null, scheduler: scheduler, isKnownModel: null, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_send", new { threadId = "thr_other", message = "hi", delivery = "immediate" }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single();
        scheduler.Scheduled.Should().ContainSingle().Which.Should().Be("thr_other");
    }

    [Fact]
    public async Task ReportIntent_WhenMissingIntent_ReturnsToolFailedMissingRequired()
    {
        var exec = new SystemToolCallExecutor(threadTools: null, observer: null, lifecycle: null, scheduler: null, isKnownModel: null, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "report_intent", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("missing_required:intent");
    }

    [Fact]
    public async Task ThreadConfig_ReadOnlySelf_WhenNoSetModel_ReturnsDefault()
    {
        var exec = new SystemToolCallExecutor(threadTools: null, observer: null, lifecycle: null, scheduler: null, isKnownModel: null, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_config", new { }), CancellationToken.None);

        var json = (JsonElement)((ObservedToolCallCompleted)obs.Single(o => o is ObservedToolCallCompleted)).Result;
        json.GetProperty("model").GetString().Should().Be("default");
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolNameIsUnknown_ReturnsUnknownToolFailed()
    {
        var exec = new SystemToolCallExecutor(threadTools: null, observer: null, lifecycle: null, scheduler: null, isKnownModel: null, threadId: "thr_main");

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "nope", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("unknown_tool");
    }

    private sealed class FakeThreadTools(string model) : IThreadTools
    {
        public ThreadMetadata? Meta { get; init; }

        public void ReportIntent(string threadId, string intent) { }
        public ImmutableArray<ThreadInfo> List() => ImmutableArray<ThreadInfo>.Empty;
        public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId) => ImmutableArray<ThreadMessage>.Empty;
        public string GetModel(string threadId) => model;
        public ThreadMetadata? TryGetThreadMetadata(string threadId)
            => Meta is not null && string.Equals(Meta.ThreadId, threadId, StringComparison.Ordinal) ? Meta : null;
    }

    private sealed class FakeLifecycle : IThreadLifecycle
    {
        public bool Forked { get; private set; }
        public ImmutableArray<SessionEvent> Seed { get; private set; } = ImmutableArray<SessionEvent>.Empty;

        public Task RequestForkChildThreadAsync(string parentThreadId, string childThreadId, ThreadMode mode, ImmutableArray<SessionEvent> seedCommitted, CancellationToken cancellationToken = default)
        {
            Forked = true;
            Seed = seedCommitted;
            return Task.CompletedTask;
        }

        public Task RequestSetThreadModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestStopThreadAsync(string threadId, string? reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeObserver : IThreadObserver
    {
        public List<(string threadId, ObservedChatEvent observed)> Observed { get; } = new();

        public Task ObserveAsync(string threadId, ObservedChatEvent observed, CancellationToken cancellationToken = default)
        {
            Observed.Add((threadId, observed));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScheduler : IThreadScheduler
    {
        public List<string> Scheduled { get; } = new();
        public void ScheduleRun(string threadId) => Scheduled.Add(threadId);
    }
}
