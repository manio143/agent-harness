using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Agent.Acp.Client.AvaloniaApp.Domain.Conversation;
using Agent.Acp.Schema;

namespace Agent.Acp.Client.AvaloniaApp.Services.Sessions;

/// <summary>
/// Minimal local persistence so the client can reload a transcript after rebuild/restart.
/// Stores newline-delimited JSON records.
/// </summary>
public sealed class SessionLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _path;

    public SessionLogStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path) ?? ".");
    }

    public string Path => _path;

    public void Append(ChatEvent e)
    {
        var record = e switch
        {
            ChatUserPrompt up => new StoredRecord("user_prompt", null, JsonSerializer.Serialize(up, JsonOptions)),
            ChatSessionUpdate su => new StoredRecord("session_update", su.SessionId, JsonSerializer.Serialize(su.Update, JsonOptions)),
            _ => throw new NotSupportedException($"Unsupported event type: {e.GetType().Name}")
        };

        File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOptions) + "\n");
    }

    public IReadOnlyList<ChatEvent> LoadAll()
    {
        var list = new List<ChatEvent>();
        if (!File.Exists(_path)) return list;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var record = JsonSerializer.Deserialize<StoredRecord>(line, JsonOptions);
            if (record is null) continue;

            switch (record.Kind)
            {
                case "user_prompt":
                {
                    var up = JsonSerializer.Deserialize<ChatUserPrompt>(record.PayloadJson, JsonOptions);
                    if (up is not null) list.Add(up);
                    break;
                }

                case "session_update":
                {
                    var update = JsonSerializer.Deserialize<SessionUpdate>(record.PayloadJson, JsonOptions);
                    if (update is not null)
                    {
                        var sid = record.SessionId ?? "unknown";
                        list.Add(new ChatSessionUpdate(sid, update));
                    }
                    break;
                }
            }
        }

        return list;
    }

    private sealed record StoredRecord(string Kind, string? SessionId, string PayloadJson);
}
