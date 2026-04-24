using System.Collections.Immutable;
using Agent.Harness.Threads;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadReadToolHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ReadsMessagesFromThreadTools_AndReturnsCompleted()
    {
        var tools = new FakeThreadTools();
        var handler = new ThreadReadToolHandler(tools);

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "thread_read", new { threadId = "child-0001" }),
            CancellationToken.None);

        tools.ReadCalls.Should().ContainSingle().Which.Should().Be("child-0001");
        obs.Should().ContainSingle().Which.Should().BeOfType<ObservedToolCallCompleted>();
    }

    private sealed class FakeThreadTools : IThreadTools
    {
        public List<string> ReadCalls { get; } = new();

        public void ReportIntent(string threadId, string intent) { }
        public ImmutableArray<ThreadInfo> List() => ImmutableArray<ThreadInfo>.Empty;

        public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId)
        {
            ReadCalls.Add(threadId);
            return ImmutableArray.Create(new ThreadMessage("assistant", "hello"));
        }

        public string GetModel(string threadId) => "default";
        public ThreadMetadata? TryGetThreadMetadata(string threadId) => null;
    }
}
