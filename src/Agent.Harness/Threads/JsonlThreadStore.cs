using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Threads;

public sealed class JsonlThreadStore : IThreadStore
{
    private readonly string _rootDir;

    public JsonlThreadStore(string rootDir)
    {
        _rootDir = rootDir;
    }

    public void CreateMainIfMissing(string sessionId)
    {
        var existing = TryLoadThreadMetadata(sessionId, ThreadIds.Main);
        if (existing is not null) return;

        CreateThread(sessionId, new ThreadMetadata(
            ThreadId: ThreadIds.Main,
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Status: ThreadStatus.Idle));
    }

    public ThreadMetadata? TryLoadThreadMetadata(string sessionId, string threadId)
    {
        var path = ThreadMetaPath(sessionId, threadId);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ThreadMetadata>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public ImmutableArray<ThreadMetadata> ListThreads(string sessionId)
    {
        var dir = ThreadsDir(sessionId);
        if (!Directory.Exists(dir)) return ImmutableArray<ThreadMetadata>.Empty;

        var metas = new List<ThreadMetadata>();
        foreach (var threadDir in Directory.GetDirectories(dir))
        {
            var metaPath = Path.Combine(threadDir, "thread.json");
            if (!File.Exists(metaPath)) continue;
            var json = File.ReadAllText(metaPath);
            var m = JsonSerializer.Deserialize<ThreadMetadata>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (m is not null) metas.Add(m);
        }

        return metas.OrderBy(m => m.CreatedAtIso).ToImmutableArray();
    }

    public void CreateThread(string sessionId, ThreadMetadata metadata)
    {
        var dir = ThreadDir(sessionId, metadata.ThreadId);
        if (Directory.Exists(dir))
            throw new InvalidOperationException($"thread_already_exists:{metadata.ThreadId}");

        Directory.CreateDirectory(dir);
        SaveThreadMetadata(sessionId, metadata);
    }

    public void SaveThreadMetadata(string sessionId, ThreadMetadata metadata)
    {
        var dir = ThreadDir(sessionId, metadata.ThreadId);
        Directory.CreateDirectory(dir);

        var path = ThreadMetaPath(sessionId, metadata.ThreadId);
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public void AppendInbox(string sessionId, string threadId, ThreadEnvelope envelope)
    {
        var dir = ThreadDir(sessionId, threadId);
        Directory.CreateDirectory(dir);
        var path = InboxPath(sessionId, threadId);

        var line = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.AppendAllText(path, line + "\n");
    }

    public ImmutableArray<ThreadEnvelope> LoadInbox(string sessionId, string threadId)
    {
        var path = InboxPath(sessionId, threadId);
        if (!File.Exists(path)) return ImmutableArray<ThreadEnvelope>.Empty;

        var list = new List<ThreadEnvelope>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var env = JsonSerializer.Deserialize<ThreadEnvelope>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (env is not null) list.Add(env);
        }

        return list.ToImmutableArray();
    }

    public void SaveInbox(string sessionId, string threadId, ImmutableArray<ThreadEnvelope> envelopes)
    {
        var dir = ThreadDir(sessionId, threadId);
        Directory.CreateDirectory(dir);
        var path = InboxPath(sessionId, threadId);

        if (envelopes.IsDefaultOrEmpty)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var lines = envelopes.Select(e => JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    public void ClearInbox(string sessionId, string threadId)
    {
        var path = InboxPath(sessionId, threadId);
        if (File.Exists(path)) File.Delete(path);
    }

    public void AppendCommittedEvent(string sessionId, string threadId, SessionEvent evt)
    {
        var dir = ThreadDir(sessionId, threadId);
        Directory.CreateDirectory(dir);
        var path = EventsPath(sessionId, threadId);

        var payload = Agent.Harness.Persistence.SessionEventJson.Serialize(evt);
        File.AppendAllText(path, payload + "\n");
    }

    public ImmutableArray<SessionEvent> LoadCommittedEvents(string sessionId, string threadId)
    {
        var path = EventsPath(sessionId, threadId);
        if (!File.Exists(path)) return ImmutableArray<SessionEvent>.Empty;

        var list = new List<SessionEvent>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            list.Add(Agent.Harness.Persistence.SessionEventJson.Deserialize(line));
        }

        return list.ToImmutableArray();
    }

    private string SessionDir(string sessionId) => Path.Combine(_rootDir, sessionId);
    private string ThreadsDir(string sessionId) => Path.Combine(SessionDir(sessionId), "threads");
    private string ThreadDir(string sessionId, string threadId) => Path.Combine(ThreadsDir(sessionId), threadId);

    private string ThreadMetaPath(string sessionId, string threadId) => Path.Combine(ThreadDir(sessionId, threadId), "thread.json");
    private string InboxPath(string sessionId, string threadId) => Path.Combine(ThreadDir(sessionId, threadId), "inbox.jsonl");
    private string EventsPath(string sessionId, string threadId) => Path.Combine(ThreadDir(sessionId, threadId), "events.jsonl");
}
