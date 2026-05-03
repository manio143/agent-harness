using Agent.Acp.Client.AvaloniaApp.Domain.Conversation;
using Agent.Acp.Schema;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ChatReducerReconnectSeparatorTests
{
    [Fact]
    public void SessionIdChange_InsertsSystemSeparator()
    {
        var s = ChatState.Empty;

        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s1", new AgentMessageChunk
        {
            Content = new TextContent { Text = "Hi" }
        }));

        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s2", new AgentMessageChunk
        {
            Content = new TextContent { Text = "Again" }
        }));

        Assert.Equal(3, s.Items.Length);
        Assert.IsType<ChatText>(s.Items[0]);
        var sep = Assert.IsType<ChatSystemText>(s.Items[1]);
        Assert.Contains("Reconnected", sep.Text);
        Assert.IsType<ChatText>(s.Items[2]);
    }
}
