using Agent.Acp.Client.AvaloniaApp.Domain.Conversation;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ChatUserPromptReducerTests
{
    [Fact]
    public void UserPrompt_AddsUserTextItem()
    {
        var s = ChatState.Empty;
        s = ChatReducer.Reduce(s, new ChatUserPrompt(" hello "));

        Assert.Single(s.Items);
        var u = Assert.IsType<ChatUserText>(s.Items[0]);
        Assert.Equal("hello", u.Text);
    }
}
