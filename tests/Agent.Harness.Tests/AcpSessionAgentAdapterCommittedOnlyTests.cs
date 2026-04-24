using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpSessionAgentAdapterCommittedOnlyTests
{
    [Fact]
    public async Task PromptAsync_PublishesCommittedAssistantMessage_AsSessionUpdate()
    {
        var events = new CapturingSessionEvents();

        async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            yield return new ObservedAssistantTextDelta("hi");
            yield return new ObservedAssistantMessageCompleted();
        }

        var agent = new AcpSessionAgentAdapter(
            sessionId: "s1",
            events: events,
            observed: Observed,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false));

        var res = await agent.PromptAsync(new PromptRequest(), new FakeTurn(), CancellationToken.None);

        res.StopReason.Value.Should().Be(StopReason.EndTurn);

        events.Updates.Should().ContainSingle()
            .Which.Should().BeOfType<AgentMessageChunk>()
            .Which.Content.As<TextContent>().Text.Should().Be("hi");
    }

    [Fact]
    public async Task PromptAsync_DoesNotPublish_ObservedOnlyAssistantDelta_WhenNotCommitted()
    {
        var events = new CapturingSessionEvents();

        async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            yield return new ObservedAssistantTextDelta("hi");
            // No ObservedAssistantMessageCompleted => delta stays in buffer and is never committed.
        }

        var agent = new AcpSessionAgentAdapter(
            sessionId: "s1",
            events: events,
            observed: Observed,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false));

        var res = await agent.PromptAsync(new PromptRequest(), new FakeTurn(), CancellationToken.None);

        res.StopReason.Value.Should().Be(StopReason.EndTurn);
        events.Updates.Should().BeEmpty();
    }

    private sealed class CapturingSessionEvents : IAcpSessionEvents
    {
        public List<object> Updates { get; } = new();

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTurn : IAcpPromptTurn
    {
        public IAcpToolCalls ToolCalls { get; } = new FakeToolCalls();
    }

    private sealed class FakeToolCalls : IAcpToolCalls
    {
        public IReadOnlyCollection<string> ActiveToolCallIds => Array.Empty<string>();

        public IAcpToolCall Start(string toolCallId, string title, ToolKind kind)
            => throw new NotSupportedException();

        public Task CancelAllAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

file static class AcpSessionAgentAdapterToolContentExtensions
{
    public static T As<T>(this object o) where T : class => (T)o;
}
