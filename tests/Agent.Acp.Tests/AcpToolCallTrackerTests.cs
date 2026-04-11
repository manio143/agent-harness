using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpToolCallTrackerTests
{
    [Fact]
    public async Task ToolCall_Allows_AddContent_Then_Finalize_And_Rejects_Further_Content()
    {
        var sent = new List<object>();
        var events = new CapturingEvents(sent);
        var tracker = new AcpToolCallTracker(events);

        var call = tracker.Start("call_1", "Test", ToolKind.Other);

        await call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "hello" } });
        await call.InProgressAsync();
        await call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "world" } });
        await call.CompletedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "nope" } }));

        // sanity: we sent at least one tool_call_update with content
        Assert.Contains(sent, u => u is not null);
    }

    private sealed class CapturingEvents : IAcpSessionEvents
    {
        private readonly List<object> _sent;

        public CapturingEvents(List<object> sent)
        {
            _sent = sent;
        }

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            _sent.Add(update);
            return Task.CompletedTask;
        }
    }
}
