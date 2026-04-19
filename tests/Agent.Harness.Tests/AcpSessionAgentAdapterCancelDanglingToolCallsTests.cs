using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class AcpSessionAgentAdapterCancelDanglingToolCallsTests
{
    [Fact]
    public async Task PromptAsync_WhenTurnReportsActiveToolCalls_CancelsAll()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            await Task.CompletedTask;
            yield break;
        }

        var events = new CapturingEvents();
        var adapter = new AcpSessionAgentAdapter("s1", events, Observed);

        var turn = new FakeTurn();
        turn.ToolCallsImpl.ActiveIds.Add("dangling");

        await adapter.PromptAsync(new PromptRequest { SessionId = "s1", Prompt = new List<ContentBlock>() }, turn, CancellationToken.None);

        turn.ToolCallsImpl.CancelAllCalls.Should().Be(1);
    }

    private sealed class CapturingEvents : IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTurn : IAcpPromptTurn
    {
        public FakeToolCalls ToolCallsImpl { get; } = new();
        IAcpToolCalls IAcpPromptTurn.ToolCalls => ToolCallsImpl;
    }

    private sealed class FakeToolCalls : IAcpToolCalls
    {
        public List<string> ActiveIds { get; } = new();
        public int CancelAllCalls { get; private set; }

        public IReadOnlyCollection<string> ActiveToolCallIds => ActiveIds;

        public IAcpToolCall Start(string toolCallId, string title, ToolKind kind) => throw new NotSupportedException();

        public Task CancelAllAsync(CancellationToken cancellationToken = default)
        {
            CancelAllCalls++;
            return Task.CompletedTask;
        }
    }
}
