using System;
using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

namespace Agent.Acp.Client.AvaloniaApp.Services.Acp;

public sealed class AcpSessionUpdatePump
{
    private readonly string _sessionId;
    private readonly ChatViewModel _chat;

    public AcpSessionUpdatePump(string sessionId, ChatViewModel chat)
    {
        _sessionId = sessionId;
        _chat = chat;
    }

    public bool TryHandle(JsonRpcNotification notif)
    {
        if (!string.Equals(notif.Method, "session/update", StringComparison.Ordinal))
            return false;

        // Params: { sessionId, update }
        if (notif.Params is null)
            return false;

        var p = notif.Params.Value;
        if (p.ValueKind != JsonValueKind.Object)
            return false;

        if (!p.TryGetProperty("sessionId", out var sid) || sid.ValueKind != JsonValueKind.String)
            return false;

        if (!string.Equals(sid.GetString(), _sessionId, StringComparison.Ordinal))
            return true; // ignore other sessions

        if (!p.TryGetProperty("update", out var updateEl) || updateEl.ValueKind != JsonValueKind.Object)
            return true;

        var update = JsonSerializer.Deserialize<SessionUpdate>(updateEl.GetRawText(), AcpJson.Options);
        if (update is null)
            return true;

        _chat.Apply(update);
        return true;
    }
}
