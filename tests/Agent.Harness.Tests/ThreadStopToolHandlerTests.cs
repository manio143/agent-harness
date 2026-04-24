using System.Collections.Immutable;
using Agent.Harness.Threads;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadStopToolHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_StopsTargetThread_AndReturnsCompleted()
    {
        var lifecycle = new FakeLifecycle();
        var handler = new ThreadStopToolHandler(lifecycle, currentThreadId: "main");

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_stop", new { threadId = "child", reason = "done" }),
            CancellationToken.None);

        lifecycle.StopCalls.Should().ContainSingle().Which.Should().Be(("child", "done"));
        obs.Should().ContainSingle().Which.Should().BeOfType<ObservedToolCallCompleted>();
    }

    private sealed class FakeLifecycle : IThreadLifecycle
    {
        public List<(string threadId, string? reason)> StopCalls { get; } = new();

        public Task RequestForkChildThreadAsync(string parentThreadId, string childThreadId, ThreadMode mode, ImmutableArray<SessionEvent> seedCommitted, ThreadCapabilitiesSpec? capabilities, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestSetThreadModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestStopThreadAsync(string threadId, string? reason, CancellationToken cancellationToken = default)
        {
            StopCalls.Add((threadId, reason));
            return Task.CompletedTask;
        }
    }
}
