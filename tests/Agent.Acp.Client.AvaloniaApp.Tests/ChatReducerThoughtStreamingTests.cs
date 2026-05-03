using Agent.Acp.Client.AvaloniaApp.Domain.Conversation;
using Agent.Acp.Schema;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ChatReducerThoughtStreamingTests
{
    [Fact]
    public void AgentThoughtChunk_ConsecutiveChunks_AreAppendedToLastChatThought()
    {
        var s = ChatState.Empty;

        s = ChatReducer.Reduce(s, new ChatSessionUpdate(new AgentThoughtChunk
        {
            Content = new TextContent { Text = "Reason" }
        }));

        s = ChatReducer.Reduce(s, new ChatSessionUpdate(new AgentThoughtChunk
        {
            Content = new TextContent { Text = "ing" }
        }));

        Assert.Single(s.Items);
        var t = Assert.IsType<ChatThought>(s.Items[0]);
        Assert.Equal("Reasoning", t.Text);
    }
}
