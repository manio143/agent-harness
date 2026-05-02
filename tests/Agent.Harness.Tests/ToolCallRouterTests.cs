using System.Collections.Immutable;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolCallRouterTests
{
    private sealed class ThrowingExecutor : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => true;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ExecuteAsync_WhenExecutorThrows_ReturnsObservedToolCallFailed()
    {
        var router = new ToolCallRouter(new IToolCallExecutor[] { new ThrowingExecutor() });

        var state = SessionState.Empty;
        var effect = new ExecuteToolCall("tool1", "some_tool", new { });

        var obs = await router.ExecuteAsync(state, effect, CancellationToken.None);

        obs.Should().ContainSingle();
        obs[0].Should().BeOfType<ObservedToolCallFailed>();
        ((ObservedToolCallFailed)obs[0]).ToolId.Should().Be("tool1");
        ((ObservedToolCallFailed)obs[0]).Error.Should().Contain("boom");
    }
}
