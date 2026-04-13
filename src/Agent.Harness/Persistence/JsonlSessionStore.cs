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

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
                continue;

            var type = typeEl.GetString();

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

                case "session_title_set":
                    list.Add(new SessionTitleSet(root.GetProperty("title").GetString() ?? string.Empty));
                    break;

                case "turn_started":
                    list.Add(new TurnStarted());
                    break;

                case "turn_ended":
                    list.Add(new TurnEnded());
                    break;

                // Tool calling lifecycle
                case "tool_call_requested":
                    list.Add(new ToolCallRequested(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        ToolName: root.GetProperty("toolName").GetString() ?? string.Empty,
                        Args: root.GetProperty("args").Clone()));
                    break;

                case "tool_call_permission_approved":
                    list.Add(new ToolCallPermissionApproved(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        Reason: root.GetProperty("reason").GetString() ?? string.Empty));
                    break;

                case "tool_call_permission_denied":
                    list.Add(new ToolCallPermissionDenied(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        Reason: root.GetProperty("reason").GetString() ?? string.Empty));
                    break;

                case "tool_call_pending":
                    list.Add(new ToolCallPending(root.GetProperty("toolId").GetString() ?? string.Empty));
                    break;

                case "tool_call_in_progress":
                    list.Add(new ToolCallInProgress(root.GetProperty("toolId").GetString() ?? string.Empty));
                    break;

                case "tool_call_update":
                    list.Add(new ToolCallUpdate(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        Content: root.GetProperty("content").Clone()));
                    break;

                case "tool_call_completed":
                    list.Add(new ToolCallCompleted(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        Result: root.GetProperty("result").Clone()));
                    break;

                case "tool_call_failed":
                    list.Add(new ToolCallFailed(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        Error: root.GetProperty("error").GetString() ?? string.Empty));
                    break;

                case "tool_call_cancelled":
                    list.Add(new ToolCallCancelled(root.GetProperty("toolId").GetString() ?? string.Empty));
                    break;

                case "tool_call_rejected":
                {
                    var details = ImmutableArray.CreateBuilder<string>();
                    if (root.TryGetProperty("details", out var detailsEl) && detailsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in detailsEl.EnumerateArray())
                        {
                            if (d.ValueKind == JsonValueKind.String)
                                details.Add(d.GetString() ?? string.Empty);
                        }
                    }

                    list.Add(new ToolCallRejected(
                        ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                        Reason: root.GetProperty("reason").GetString() ?? string.Empty,
                        Details: details.ToImmutable()));
                    break;
                }

                // Forward-compat: ignore unknown event types.
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

            object? payload = evt switch
            {
                UserMessage u => new { type = "user_message", text = u.Text },
                AssistantMessage a => new { type = "assistant_message", text = a.Text },
                AssistantTextDelta d => new { type = "assistant_text_delta", textDelta = d.TextDelta },
                ReasoningTextDelta r => new { type = "reasoning_text_delta", textDelta = r.TextDelta },
                SessionTitleSet t => new { type = "session_title_set", title = t.Title },
                TurnStarted => new { type = "turn_started" },
                TurnEnded => new { type = "turn_ended" },

                ToolCallRequested r => new { type = "tool_call_requested", toolId = r.ToolId, toolName = r.ToolName, args = r.Args },
                ToolCallPermissionApproved a => new { type = "tool_call_permission_approved", toolId = a.ToolId, reason = a.Reason },
                ToolCallPermissionDenied d => new { type = "tool_call_permission_denied", toolId = d.ToolId, reason = d.Reason },
                ToolCallPending p => new { type = "tool_call_pending", toolId = p.ToolId },
                ToolCallInProgress ip => new { type = "tool_call_in_progress", toolId = ip.ToolId },
                ToolCallUpdate u => new { type = "tool_call_update", toolId = u.ToolId, content = u.Content },
                ToolCallCompleted c => new { type = "tool_call_completed", toolId = c.ToolId, result = c.Result },
                ToolCallFailed f => new { type = "tool_call_failed", toolId = f.ToolId, error = f.Error },
                ToolCallCancelled c => new { type = "tool_call_cancelled", toolId = c.ToolId },
                ToolCallRejected r => new { type = "tool_call_rejected", toolId = r.ToolId, reason = r.Reason, details = r.Details },

                _ => null,
            };

            if (payload is null)
                return;

            var line = JsonSerializer.Serialize(payload, JsonOptions);
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
