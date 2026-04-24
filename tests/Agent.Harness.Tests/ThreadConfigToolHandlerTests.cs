using System.Collections.Immutable;
using Agent.Harness.Threads;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadConfigToolHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_WhenModelProvidedOnSameThread_EmitsObservedSetModel()
    {
        var handler = new ThreadConfigToolHandler(
            threadTools: null,
            lifecycle: null,
            currentThreadId: "main",
            isKnownModel: _ => true);

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_config", new { model = "m2" }),
            CancellationToken.None);

        obs.Should().ContainSingle(o => o is ObservedSetModel);
        obs.Should().ContainSingle(o => o is ObservedToolCallCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCrossThreadAndModelProvided_PersistsViaLifecycle()
    {
        var lifecycle = new FakeLifecycle();
        var handler = new ThreadConfigToolHandler(
            threadTools: null,
            lifecycle: lifecycle,
            currentThreadId: "main",
            isKnownModel: _ => true);

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_config", new { threadId = "child", model = "default" }),
            CancellationToken.None);

        lifecycle.SetModelCalls.Should().ContainSingle().Which.Should().Be(("child", "default"));
        obs.Should().ContainSingle(o => o is ObservedToolCallCompleted);
        obs.Should().NotContain(o => o is ObservedSetModel);
    }

    private sealed class FakeLifecycle : IThreadLifecycle
    {
        public List<(string threadId, string model)> SetModelCalls { get; } = new();

        public Task RequestForkChildThreadAsync(string parentThreadId, string childThreadId, ThreadMode mode, ImmutableArray<SessionEvent> seedCommitted, ThreadCapabilitiesSpec? capabilities, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestSetThreadModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
        {
            SetModelCalls.Add((threadId, model));
            return Task.CompletedTask;
        }

        public Task RequestStopThreadAsync(string threadId, string? reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
