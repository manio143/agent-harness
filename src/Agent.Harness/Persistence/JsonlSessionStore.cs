using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Persistence;

/// <summary>
/// Append-only JSONL store for committed session events + a small session metadata file.
///
/// - events: {root}/{sessionId}/events.jsonl
/// - metadata: {root}/{sessionId}/session.json
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _rootDir;
    private readonly ConcurrentDictionary<string, object> _locks = new();

    public JsonlSessionStore(string rootDir)
    {
        _rootDir = rootDir;
    }

    public string RootDir => _rootDir;

    public void CreateNew(string sessionId, SessionMetadata metadata)
    {
        EnsureRoot();
        Directory.CreateDirectory(GetSessionDir(sessionId));

        WriteMetadata(sessionId, metadata);

        var eventsPath = GetEventsPath(sessionId);
        if (!File.Exists(eventsPath))
            File.WriteAllText(eventsPath, string.Empty);
    }

    public bool Exists(string sessionId) => File.Exists(GetEventsPath(sessionId));

    public ImmutableArray<string> ListSessionIds()
    {
        EnsureRoot();
        if (!Directory.Exists(_rootDir))
            return ImmutableArray<string>.Empty;

        return Directory.EnumerateDirectories(_rootDir)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public SessionMetadata? TryLoadMetadata(string sessionId)
    {
        var path = GetMetadataPath(sessionId);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionMetadata>(json, JsonOptions);
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

            try
            {
                list.Add(SessionEventJson.Deserialize(line));
            }
            catch
            {
                // Forward-compat: ignore unknown/bad event types.
            }
        }

        return list.ToImmutableArray();
    }

    public void AppendCommitted(string sessionId, SessionEvent evt)
    {
        var gate = _locks.GetOrAdd(sessionId, _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(GetSessionDir(sessionId));

            var line = SessionEventJson.Serialize(evt);
            if (string.IsNullOrWhiteSpace(line))
                return;

            File.AppendAllText(GetEventsPath(sessionId), line + "\n");

            // Best-effort metadata projection (updatedAt bump + title updates).
            var meta = TryLoadMetadata(sessionId);
            if (meta is not null)
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                var projected = evt switch
                {
                    SessionTitleSet t => meta with { Title = t.Title, UpdatedAtIso = now },
                    _ => meta with { UpdatedAtIso = now },
                };

                WriteMetadata(sessionId, projected);
            }
        }
    }

    public void UpdateMetadata(string sessionId, SessionMetadata metadata)
    {
        var gate = _locks.GetOrAdd(sessionId, _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(GetSessionDir(sessionId));
            WriteMetadata(sessionId, metadata);
        }
    }

    private void WriteMetadata(string sessionId, SessionMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(GetMetadataPath(sessionId), json);
    }

    private void EnsureRoot() => Directory.CreateDirectory(_rootDir);

    private string GetSessionDir(string sessionId) => Path.Combine(_rootDir, sessionId);

    private string GetEventsPath(string sessionId) => Path.Combine(GetSessionDir(sessionId), "events.jsonl");

    private string GetMetadataPath(string sessionId) => Path.Combine(GetSessionDir(sessionId), "session.json");
}
