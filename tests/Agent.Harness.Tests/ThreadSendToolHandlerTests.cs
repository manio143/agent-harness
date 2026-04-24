using System.Collections.Immutable;
using Agent.Harness.Threads;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadSendToolHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_SendToSelf_DoesNotRequireOrchestrator_AndEmitsInboxArrival()
    {
        var handler = new ThreadSendToolHandler(
            threadTools: null,
            observer: null,
            scheduler: null,
            currentThreadId: "main");

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_send", new { threadId = "main", message = "hi" }),
            CancellationToken.None);

        obs.Should().ContainSingle(o => o is ObservedInboxMessageArrived);
        obs.Should().ContainSingle(o => o is ObservedToolCallCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_CrossThread_RequiresThreadToolsForValidation()
    {
        var handler = new ThreadSendToolHandler(
            threadTools: null,
            observer: null,
            scheduler: null,
            currentThreadId: "main");

        var act = async () => await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_send", new { threadId = "child", message = "hi" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("thread_tools_require_orchestrator");
    }

}
