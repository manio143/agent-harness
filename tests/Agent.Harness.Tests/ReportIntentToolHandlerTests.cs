using System.Collections.Immutable;
using Agent.Harness.Tools.Handlers;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ReportIntentToolHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_CallsThreadToolsAndReturnsOk()
    {
        var threadTools = new FakeThreadTools();
        var handler = new ReportIntentToolHandler(threadTools, threadId: "main");

        var obs = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "report_intent", new { intent = "do thing" }),
            CancellationToken.None);

        threadTools.Reported.Should().Be(("main", "do thing"));
        obs.Should().ContainSingle().Which.Should().BeOfType<ObservedToolCallCompleted>();
    }

    private sealed class FakeThreadTools : IThreadTools
    {
        public (string threadId, string intent)? Reported { get; private set; }

        public void ReportIntent(string threadId, string intent) => Reported = (threadId, intent);
        public ImmutableArray<ThreadInfo> List() => ImmutableArray<ThreadInfo>.Empty;
        public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId) => ImmutableArray<ThreadMessage>.Empty;
        public string GetModel(string threadId) => "default";
        public ThreadMetadata? TryGetThreadMetadata(string threadId) => null;
    }
}
