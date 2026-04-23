using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Harness.Compaction;

/// <summary>
/// Builds a compaction transcript from committed events.
///
/// IMPORTANT: tool outputs/results must NOT be included verbatim.
/// Only tool name + args + terminal status (+ short error for failures/rejections) are included.
/// </summary>
public static class CompactionTranscriptBuilder
{
    public static string Build(ImmutableArray<SessionEvent> committed)
    {
        // Range: start after last ThreadCompacted (if any)
        var start = 0;
        for (var i = committed.Length - 1; i >= 0; i--)
        {
            if (committed[i] is ThreadCompacted)
            {
                start = i + 1;
                break;
            }
        }

        var sb = new StringBuilder();

        for (var i = start; i < committed.Length; i++)
        {
            switch (committed[i])
            {
                case UserMessage u:
                    sb.AppendLine($"user: {u.Text}");
                    break;

                case AssistantMessage a:
                    sb.AppendLine($"assistant: {a.Text}");
                    break;

                case ToolCallRequested r:
                    sb.AppendLine($"tool_call: id={r.ToolId} name={r.ToolName} args={Json(SummarizeArgsIfNeeded(r.ToolName, r.Args))}");
                    break;

                case ToolCallCompleted c:
                    sb.AppendLine($"tool_result: id={c.ToolId} status=completed");
                    break;

                case ToolCallFailed f:
                    sb.AppendLine($"tool_result: id={f.ToolId} status=failed error={f.Error}");
                    break;

                case ToolCallCancelled c:
                    sb.AppendLine($"tool_result: id={c.ToolId} status=cancelled");
                    break;

                case ToolCallRejected r:
                    sb.AppendLine($"tool_result: id={r.ToolId} status=rejected reason={r.Reason}");
                    break;

                default:
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Json(JsonElement el)
    {
        try
        {
            return el.ValueKind == JsonValueKind.Undefined ? "null" : el.GetRawText();
        }
        catch
        {
            return el.ToString();
        }
    }

    private const int MaxLargeStringChars = 2000;

    private static JsonElement SummarizeArgsIfNeeded(string toolName, JsonElement args)
    {
        // Only skip large file contents for known file-writing tools.
        // For all other tools (including MCP tools), args are preserved as-is.
        if (!string.Equals(toolName, ToolSchemas.WriteTextFile.Name, StringComparison.Ordinal) &&
            !string.Equals(toolName, ToolSchemas.PatchTextFile.Name, StringComparison.Ordinal))
            return args;

        try
        {
            var node = JsonNode.Parse(args.GetRawText());
            if (node is not JsonObject obj)
                return args;

            if (string.Equals(toolName, ToolSchemas.WriteTextFile.Name, StringComparison.Ordinal))
            {
                if (obj["content"] is JsonValue v && v.TryGetValue<string>(out var content) && content is not null && content.Length > MaxLargeStringChars)
                    obj["content"] = $"<omitted length={content.Length}>";
            }

            if (string.Equals(toolName, ToolSchemas.PatchTextFile.Name, StringComparison.Ordinal))
            {
                // Patch payload can be large: omit large strings inside edits.
                TruncateLargeStrings(obj);
            }

            return JsonSerializer.SerializeToElement(obj);
        }
        catch
        {
            return args;
        }

        static void TruncateLargeStrings(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kvp => kvp.Key).ToArray())
                    {
                        var child = o[key];
                        if (child is JsonValue v && v.TryGetValue<string>(out var s) && s is not null && s.Length > MaxLargeStringChars)
                            o[key] = $"<omitted length={s.Length}>";
                        else
                            TruncateLargeStrings(child);
                    }

                    break;

                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        var child = a[i];
                        if (child is JsonValue v && v.TryGetValue<string>(out var s) && s is not null && s.Length > MaxLargeStringChars)
                            a[i] = $"<omitted length={s.Length}>";
                        else
                            TruncateLargeStrings(child);
                    }

                    break;
            }
        }
    }
}
