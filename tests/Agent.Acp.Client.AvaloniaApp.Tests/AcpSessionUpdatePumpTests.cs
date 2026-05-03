using System;
using System.Text.Json;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class AcpSessionUpdatePumpTests
{
    [Fact]
    public void Applies_agent_message_chunk_to_chat_transcript()
    {
        var chat = new ChatViewModel();
        var pump = new AcpSessionUpdatePump("s1", chat);

        var notif = new JsonRpcNotification
        {
            Method = "session/update",
            Params = JsonDocument.Parse("{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hi\"}}}").RootElement.Clone(),
        };

        Assert.True(pump.TryHandle(notif));
        Assert.Contains(chat.Transcript, x => x is string s && s.Contains("hi", StringComparison.Ordinal));
    }

    [Fact]
    public void Ignores_other_session_updates()
    {
        var chat = new ChatViewModel();
        var pump = new AcpSessionUpdatePump("s1", chat);

        var notif = new JsonRpcNotification
        {
            Method = "session/update",
            Params = JsonDocument.Parse("{\"sessionId\":\"s2\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hi\"}}}").RootElement.Clone(),
        };

        Assert.True(pump.TryHandle(notif));
        Assert.Empty(chat.Transcript);
    }
}
