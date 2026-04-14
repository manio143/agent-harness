using System.Text.Json;

namespace Agent.Harness;

public static class ObservedEventJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToJsonl(ObservedChatEvent e)
    {
        object obj = e switch
        {
            ObservedTurnStarted => new Dictionary<string, object?> { ["type"] = "obs_turn_started" },
            ObservedTurnStabilized => new Dictionary<string, object?> { ["type"] = "obs_turn_stabilized" },
            ObservedUserMessage m => new Dictionary<string, object?> { ["type"] = "obs_user_message", ["text"] = m.Text },
            ObservedAssistantTextDelta d => new Dictionary<string, object?> { ["type"] = "obs_assistant_text_delta", ["textDelta"] = d.Text },
            ObservedReasoningTextDelta d => new Dictionary<string, object?> { ["type"] = "obs_reasoning_text_delta", ["textDelta"] = d.Text },
            ObservedReasoningMessageCompleted c => new Dictionary<string, object?> { ["type"] = "obs_reasoning_message_completed", ["finishReason"] = c.FinishReason },
            ObservedAssistantMessageCompleted c => new Dictionary<string, object?> { ["type"] = "obs_assistant_message_completed", ["finishReason"] = c.FinishReason },

            ObservedToolCallDetected t => new Dictionary<string, object?>
            {
                ["type"] = "obs_tool_call_detected",
                ["toolId"] = t.ToolId,
                ["toolName"] = t.ToolName,
                ["args"] = JsonSerializer.SerializeToElement(t.Args, JsonOptions),
            },

            ObservedPermissionApproved p => new Dictionary<string, object?> { ["type"] = "obs_permission_approved", ["toolId"] = p.ToolId, ["reason"] = p.Reason },
            ObservedPermissionDenied p => new Dictionary<string, object?> { ["type"] = "obs_permission_denied", ["toolId"] = p.ToolId, ["reason"] = p.Reason },

            ObservedToolCallProgressUpdate u => new Dictionary<string, object?>
            {
                ["type"] = "obs_tool_call_progress",
                ["toolId"] = u.ToolId,
                ["content"] = JsonSerializer.SerializeToElement(u.Content, JsonOptions),
            },

            ObservedToolCallCompleted u => new Dictionary<string, object?>
            {
                ["type"] = "obs_tool_call_completed",
                ["toolId"] = u.ToolId,
                ["result"] = JsonSerializer.SerializeToElement(u.Result, JsonOptions),
            },

            ObservedToolCallFailed f => new Dictionary<string, object?> { ["type"] = "obs_tool_call_failed", ["toolId"] = f.ToolId, ["error"] = f.Error },
            ObservedToolCallCancelled c => new Dictionary<string, object?> { ["type"] = "obs_tool_call_cancelled", ["toolId"] = c.ToolId },

            ObservedMcpConnectionFailed m => new Dictionary<string, object?> { ["type"] = "obs_mcp_connection_failed", ["serverId"] = m.ServerId, ["error"] = m.Error },

            _ => new Dictionary<string, object?> { ["type"] = "obs_unknown", ["kind"] = e.GetType().Name },
        };

        var rawType = e.RawUpdate?.GetType().FullName;
        object wrapped = rawType is null
            ? obj
            : new Dictionary<string, object?> { ["rawType"] = rawType, ["event"] = obj };

        return JsonSerializer.Serialize(wrapped, JsonOptions);
    }
}
