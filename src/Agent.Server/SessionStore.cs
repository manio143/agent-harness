using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;

namespace Agent.Server;

/// <summary>
/// Very small, append-only JSONL session store.
///
/// Purpose:
/// - Persist committed events (publishable truth) so a session can be resumed.
/// - Load by replaying committed events into SessionState.
///
/// Format: one JSON object per line with a stable "type" discriminator.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _rootDir;
    private readonly ConcurrentDictionary<string, object> _locks = new();

    public SessionStore(string rootDir)
    {
        _rootDir = rootDir;
    }

    public string RootDir => _rootDir;

    public string GetSessionDir(string sessionId) => Path.Combine(_rootDir, sessionId);

    public string GetEventsPath(string sessionId) => Path.Combine(GetSessionDir(sessionId), "events.jsonl");

    public void EnsureRootDir() => Directory.CreateDirectory(_rootDir);

    public void CreateNew(string sessionId)
    {
        EnsureRootDir();
        Directory.CreateDirectory(GetSessionDir(sessionId));

        // Touch the file so existence is a quick proxy for session existence.
        var path = GetEventsPath(sessionId);
        if (!File.Exists(path))
            File.WriteAllText(path, string.Empty);
    }

    public bool Exists(string sessionId) => File.Exists(GetEventsPath(sessionId));

    public ImmutableArray<string> ListSessions()
    {
        EnsureRootDir();
        if (!Directory.Exists(_rootDir))
            return ImmutableArray<string>.Empty;

        var ids = Directory.EnumerateDirectories(_rootDir)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableArray();

        return ids;
    }

    public ImmutableArray<SessionEvent> LoadCommitted(string sessionId)
    {
        var path = GetEventsPath(sessionId);
        if (!File.Exists(path))
            return ImmutableArray<SessionEvent>.Empty;

        var list = new List<SessionEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "user_message":
                    list.Add(new UserMessage(root.GetProperty("text").GetString() ?? string.Empty));
                    break;

                case "assistant_message":
                    list.Add(new AssistantMessage(root.GetProperty("text").GetString() ?? string.Empty));
                    break;

                case "assistant_text_delta":
                    list.Add(new AssistantTextDelta(root.GetProperty("textDelta").GetString() ?? string.Empty));
                    break;

                case "reasoning_text_delta":
                    list.Add(new ReasoningTextDelta(root.GetProperty("textDelta").GetString() ?? string.Empty));
                    break;

                // Back-compat (pre-rename)
                case "user_message_added":
                    list.Add(new UserMessage(root.GetProperty("text").GetString() ?? string.Empty));
                    break;

                case "assistant_message_added":
                    list.Add(new AssistantMessage(root.GetProperty("text").GetString() ?? string.Empty));
                    break;

                case "assistant_message_delta_added":
                    list.Add(new AssistantTextDelta(root.GetProperty("textDelta").GetString() ?? string.Empty));
                    break;

                case "reasoning_delta_added":
                    list.Add(new ReasoningTextDelta(root.GetProperty("textDelta").GetString() ?? string.Empty));
                    break;

                // Intentionally ignore unknown event types for forward compatibility.
            }
        }

        return list.ToImmutableArray();
    }

    public void Append(string sessionId, SessionEvent evt)
    {
        var gate = _locks.GetOrAdd(sessionId, _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(GetSessionDir(sessionId));

            object? payload = evt switch
            {
                UserMessage u => new { type = "user_message", text = u.Text },
                AssistantMessage a => new { type = "assistant_message", text = a.Text },
                AssistantTextDelta d => new { type = "assistant_text_delta", textDelta = d.TextDelta },
                ReasoningTextDelta r => new { type = "reasoning_text_delta", textDelta = r.TextDelta },
                _ => null,
            };

            if (payload is null)
                return;

            var line = JsonSerializer.Serialize(payload, JsonOptions);
            File.AppendAllText(GetEventsPath(sessionId), line + "\n");
        }
    }
}
