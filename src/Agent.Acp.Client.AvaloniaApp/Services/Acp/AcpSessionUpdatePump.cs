using System;
using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Avalonia;
using Avalonia.Threading;

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
        => TryHandle(notif, out _);

    public bool TryHandle(JsonRpcNotification notif, out SessionUpdate? handledUpdate)
    {
        handledUpdate = null;
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

        try
        {
            // In the real app, notifications can arrive on a background thread.
            // Updating ObservableCollection off-UI-thread can throw and (worse) break the JSON-RPC receive loop,
            // which would cause `session/load` to appear to "hang".
            // In non-UI test contexts there may be no Avalonia Application/dispatcher loop; apply directly.
            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            {
                _chat.Apply(update);
            }
            else
            {
                // In the real app, marshal onto the UI thread to avoid cross-thread ObservableCollection access.
                Dispatcher.UIThread.Post(() => _chat.Apply(update));
            }
        }
        catch
        {
            // Never let UI/update exceptions kill the transport/receive loop.
        }

        handledUpdate = update;
        return true;
    }
}
