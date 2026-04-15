using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Persistence;

public static class SessionEventJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(SessionEvent evt)
    {
        object? payload = evt switch
        {
            UserMessage u => new { type = "user_message", text = u.Text },
            AssistantMessage a => new { type = "assistant_message", text = a.Text },
            AssistantTextDelta d => new { type = "assistant_text_delta", textDelta = d.TextDelta },
            ReasoningTextDelta r => new { type = "reasoning_text_delta", textDelta = r.TextDelta },
            ReasoningMessage r => new { type = "reasoning_message", text = r.Text },
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

            ThreadIntentReported i => new { type = "thread_intent_reported", intent = i.Intent },
            ThreadInboxMessageEnqueued e => new
            {
                type = "thread_inbox_message_enqueued",
                threadId = e.ThreadId,
                envelopeId = e.EnvelopeId,
                source = e.Source,
                sourceThreadId = e.SourceThreadId,
                delivery = e.Delivery,
                enqueuedAtIso = e.EnqueuedAtIso,
                text = e.Text,
            },
            ThreadInboxMessageDequeued d => new
            {
                type = "thread_inbox_message_dequeued",
                threadId = d.ThreadId,
                envelopeId = d.EnvelopeId,
                dequeuedAtIso = d.DequeuedAtIso,
            },

            _ => null,
        };

        return payload is null ? string.Empty : JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static SessionEvent Deserialize(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            throw new InvalidOperationException("missing_type");

        var type = typeEl.GetString();

        switch (type)
        {
            case "user_message":
                return new UserMessage(root.GetProperty("text").GetString() ?? string.Empty);

            case "assistant_message":
                return new AssistantMessage(root.GetProperty("text").GetString() ?? string.Empty);

            case "assistant_text_delta":
                return new AssistantTextDelta(root.GetProperty("textDelta").GetString() ?? string.Empty);

            case "reasoning_text_delta":
                return new ReasoningTextDelta(root.GetProperty("textDelta").GetString() ?? string.Empty);

            case "reasoning_message":
                return new ReasoningMessage(root.GetProperty("text").GetString() ?? string.Empty);

            case "session_title_set":
                return new SessionTitleSet(root.GetProperty("title").GetString() ?? string.Empty);

            case "turn_started":
                return new TurnStarted();

            case "turn_ended":
                return new TurnEnded();

            case "tool_call_requested":
                return new ToolCallRequested(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    ToolName: root.GetProperty("toolName").GetString() ?? string.Empty,
                    Args: root.GetProperty("args").Clone());

            case "tool_call_permission_approved":
                return new ToolCallPermissionApproved(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    Reason: root.GetProperty("reason").GetString() ?? string.Empty);

            case "tool_call_permission_denied":
                return new ToolCallPermissionDenied(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    Reason: root.GetProperty("reason").GetString() ?? string.Empty);

            case "tool_call_pending":
                return new ToolCallPending(root.GetProperty("toolId").GetString() ?? string.Empty);

            case "tool_call_in_progress":
                return new ToolCallInProgress(root.GetProperty("toolId").GetString() ?? string.Empty);

            case "tool_call_update":
                return new ToolCallUpdate(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    Content: root.GetProperty("content").Clone());

            case "tool_call_completed":
                return new ToolCallCompleted(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    Result: root.GetProperty("result").Clone());

            case "tool_call_failed":
                return new ToolCallFailed(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    Error: root.GetProperty("error").GetString() ?? string.Empty);

            case "tool_call_cancelled":
                return new ToolCallCancelled(root.GetProperty("toolId").GetString() ?? string.Empty);

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

                return new ToolCallRejected(
                    ToolId: root.GetProperty("toolId").GetString() ?? string.Empty,
                    Reason: root.GetProperty("reason").GetString() ?? string.Empty,
                    Details: details.ToImmutable());
            }

            case "thread_intent_reported":
                return new ThreadIntentReported(root.GetProperty("intent").GetString() ?? string.Empty);

            case "thread_inbox_message_enqueued":
                return new ThreadInboxMessageEnqueued(
                    ThreadId: root.GetProperty("threadId").GetString() ?? string.Empty,
                    EnvelopeId: root.GetProperty("envelopeId").GetString() ?? string.Empty,
                    Source: root.GetProperty("source").GetString() ?? string.Empty,
                    SourceThreadId: root.TryGetProperty("sourceThreadId", out var st) ? st.GetString() : null,
                    Delivery: root.GetProperty("delivery").GetString() ?? string.Empty,
                    EnqueuedAtIso: root.GetProperty("enqueuedAtIso").GetString() ?? string.Empty,
                    Text: root.GetProperty("text").GetString() ?? string.Empty);

            case "thread_inbox_message_dequeued":
                return new ThreadInboxMessageDequeued(
                    ThreadId: root.GetProperty("threadId").GetString() ?? string.Empty,
                    EnvelopeId: root.GetProperty("envelopeId").GetString() ?? string.Empty,
                    DequeuedAtIso: root.GetProperty("dequeuedAtIso").GetString() ?? string.Empty);

            // Back-compat: older name
            case "thread_inbox_message_drained_for_prompt":
                return new ThreadInboxMessageDequeued(
                    ThreadId: root.GetProperty("threadId").GetString() ?? string.Empty,
                    EnvelopeId: root.GetProperty("envelopeId").GetString() ?? string.Empty,
                    DequeuedAtIso: root.GetProperty("drainedAtIso").GetString() ?? string.Empty);

            // Back-compat: older name
            case "thread_inbox_message_delivered_to_llm":
                return new ThreadInboxMessageDequeued(
                    ThreadId: root.GetProperty("threadId").GetString() ?? string.Empty,
                    EnvelopeId: root.GetProperty("envelopeId").GetString() ?? string.Empty,
                    DequeuedAtIso: root.GetProperty("deliveredAtIso").GetString() ?? string.Empty);

            default:
                throw new InvalidOperationException($"unknown_event_type:{type}");
        }
    }
}
