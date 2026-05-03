using Agent.Acp.Client.AvaloniaApp.Domain.Conversation;
using Agent.Acp.Schema;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ChatReducerStreamingTests
{
    [Fact]
    public void AgentMessageChunk_ConsecutiveChunks_AreAppendedToLastChatText()
    {
        var s = ChatState.Empty;

        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s1", new AgentMessageChunk
        {
            Content = new TextContent { Text = "Hel" }
        }));

        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s1", new AgentMessageChunk
        {
            Content = new TextContent { Text = "lo" }
        }));

        Assert.Single(s.Items);
        var t = Assert.IsType<ChatText>(s.Items[0]);
        Assert.Equal("Hello", t.Text);
    }

    [Fact]
    public void AgentMessageChunk_AfterNonChunk_StartsNewChatText()
    {
        var s = ChatState.Empty;

        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s1", new AgentMessageChunk
        {
            Content = new TextContent { Text = "Hello" }
        }));

        // Any non-agent_message_chunk update breaks the stream.
        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s1", new ToolCall
        {
            ToolCallId = "t1",
            Title = "read_text_file",
            Kind = ToolKind.Read,
            Status = ToolCallStatus.InProgress,
            RawInput = new object(),
            RawOutput = new object(),
            Content = [],
            Locations = [],
        }));

        s = ChatReducer.Reduce(s, new ChatSessionUpdate("s1", new AgentMessageChunk
        {
            Content = new TextContent { Text = " world" }
        }));

        Assert.Equal(3, s.Items.Length); // "Hello" + intent/tool group (maybe) + " world" (new text)
        Assert.IsType<ChatText>(s.Items[0]);
        Assert.IsType<ChatText>(s.Items[^1]);
    }
}
