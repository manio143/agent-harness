using System.Collections.Immutable;
using Agent.Harness.Threads;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadListToolHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsThreadsFromThreadTools()
    {
        var tools = new FakeThreadTools();
        var handler = new ThreadListToolHandler(tools);

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_list", new { }),
            CancellationToken.None);

        obs.Should().ContainSingle().Which.Should().BeOfType<ObservedToolCallCompleted>();
        tools.ListCalls.Should().Be(1);
    }

    private sealed class FakeThreadTools : IThreadTools
    {
        public int ListCalls { get; private set; }

        public void ReportIntent(string threadId, string intent) { }

        public ImmutableArray<ThreadInfo> List()
        {
            ListCalls++;
            return ImmutableArray.Create(new ThreadInfo(
                ThreadId: "child-0001",
                ParentThreadId: ThreadIds.Main,
                Status: ThreadStatus.Running,
                Mode: ThreadMode.Multi,
                Intent: "test",
                Model: "default"));
        }

        public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId) => ImmutableArray<ThreadMessage>.Empty;
        public string GetModel(string threadId) => "default";
        public ThreadMetadata? TryGetThreadMetadata(string threadId) => null;
    }
}
