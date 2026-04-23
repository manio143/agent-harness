using System.Text.Json;

namespace Agent.Harness;

public static class ObservedEventJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToJsonl(ObservedChatEvent e)
    {
        object obj = e switch
        {
            ObservedTurnStarted s => new Dictionary<string, object?> { ["type"] = "obs_turn_started", ["threadId"] = s.ThreadId },
            ObservedTurnStabilized s => new Dictionary<string, object?> { ["type"] = "obs_turn_stabilized", ["threadId"] = s.ThreadId },
            ObservedWakeModel w => new Dictionary<string, object?> { ["type"] = "obs_wake_model", ["threadId"] = w.ThreadId },
            ObservedUserMessage m => new Dictionary<string, object?> { ["type"] = "obs_user_message", ["text"] = m.Text },
            ObservedAssistantTextDelta d => new Dictionary<string, object?> { ["type"] = "obs_assistant_text_delta", ["textDelta"] = d.Text },
            ObservedReasoningTextDelta d => new Dictionary<string, object?> { ["type"] = "obs_reasoning_text_delta", ["textDelta"] = d.Text },
            ObservedReasoningMessageCompleted c => new Dictionary<string, object?> { ["type"] = "obs_reasoning_message_completed", ["finishReason"] = c.FinishReason },
            ObservedAssistantMessageCompleted c => new Dictionary<string, object?> { ["type"] = "obs_assistant_message_completed", ["finishReason"] = c.FinishReason },

            ObservedTokenUsage u => new Dictionary<string, object?>
            {
                ["type"] = "obs_token_usage",
                ["inputTokens"] = u.InputTokens,
                ["outputTokens"] = u.OutputTokens,
                ["totalTokens"] = u.TotalTokens,
                ["providerModel"] = u.ProviderModel,
            },

            ObservedSetModel m => new Dictionary<string, object?> { ["type"] = "obs_set_model", ["threadId"] = m.ThreadId, ["model"] = m.Model },

            ObservedInboxMessageArrived m => new Dictionary<string, object?>
            {
                ["type"] = "obs_inbox_message_arrived",
                ["threadId"] = m.ThreadId,
                ["kind"] = m.Kind.ToString(),
                ["delivery"] = m.Delivery.ToString(),
                ["envelopeId"] = m.EnvelopeId,
                ["enqueuedAtIso"] = m.EnqueuedAtIso,
                ["source"] = m.Source,
                ["sourceThreadId"] = m.SourceThreadId,
                ["text"] = m.Text,
                ["meta"] = m.Meta,
            },

            ObservedCompactionGenerated c => new Dictionary<string, object?>
            {
                ["type"] = "obs_compaction_generated",
                ["structured"] = c.Structured,
                ["proseSummary"] = c.ProseSummary,
            },

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
