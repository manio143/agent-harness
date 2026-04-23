using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

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
                    sb.AppendLine($"tool_call: id={r.ToolId} name={r.ToolName} args={Json(r.Args)}");
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
}
