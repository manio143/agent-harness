using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Threads;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadStartCapabilitiesValidationTests
{
    [Fact]
    public async Task ThreadStart_WhenCapabilitiesContainUnknownSelector_FailsWithInvalidCapabilitySelector()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(
            threadTools: tools,
            observer: observer,
            lifecycle: lifecycle,
            scheduler: scheduler,
            threadIdAllocator: new TestThreadIdAllocator("0000"),
            isKnownModel: _ => true,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_start", new
            {
                name = "child",
                context = "new",
                mode = "multi",
                message = "hi",
                capabilities = new { allow = new[] { "fs.reed" } },
            }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_start.invalid_capability_selector:fs.reed");
        lifecycle.Forked.Should().BeFalse();
        observer.Observed.Should().BeEmpty();
        scheduler.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task ThreadStart_WhenCapabilitiesContainInvalidMcpServerId_FailsWithInvalidCapabilitySelector()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(
            threadTools: tools,
            observer: observer,
            lifecycle: lifecycle,
            scheduler: scheduler,
            threadIdAllocator: new TestThreadIdAllocator("0000"),
            isKnownModel: _ => true,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_start", new
            {
                name = "child",
                context = "new",
                mode = "multi",
                message = "hi",
                capabilities = new { deny = new[] { "mcp:" } },
            }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_start.invalid_capability_selector:mcp:");
        lifecycle.Forked.Should().BeFalse();
    }

    [Fact]
    public async Task ThreadStart_WhenCapabilitiesContainInvalidMcpToolSelector_FailsWithInvalidCapabilitySelector()
    {
        var lifecycle = new FakeLifecycle();
        var observer = new FakeObserver();
        var scheduler = new FakeScheduler();
        var tools = new FakeThreadTools(model: "default");

        var exec = new SystemToolCallExecutor(
            threadTools: tools,
            observer: observer,
            lifecycle: lifecycle,
            scheduler: scheduler,
            threadIdAllocator: new TestThreadIdAllocator("0000"),
            isKnownModel: _ => true,
            threadId: "thr_main");

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_start", new
            {
                name = "child",
                context = "new",
                mode = "multi",
                message = "hi",
                capabilities = new { deny = new[] { "mcp:files:" } },
            }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_start.invalid_capability_selector:mcp:files:");
        lifecycle.Forked.Should().BeFalse();
    }

    private sealed class FakeThreadTools(string model) : IThreadTools
    {
        public void ReportIntent(string threadId, string intent) { }
        public ImmutableArray<ThreadInfo> List() => ImmutableArray<ThreadInfo>.Empty;
        public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId) => ImmutableArray<ThreadMessage>.Empty;
        public string GetModel(string threadId) => model;
        public ThreadMetadata? TryGetThreadMetadata(string threadId) => null;
    }

    private sealed class FakeLifecycle : IThreadLifecycle
    {
        public bool Forked { get; private set; }

        public Task RequestForkChildThreadAsync(string parentThreadId, string childThreadId, ThreadMode mode, ImmutableArray<SessionEvent> seedCommitted, ThreadCapabilitiesSpec? capabilities, CancellationToken cancellationToken = default)
        {
            Forked = true;
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
