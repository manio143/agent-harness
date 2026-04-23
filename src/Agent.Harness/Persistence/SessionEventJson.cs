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
            InterThreadMessage it => new { type = "inter_thread_message", fromThreadId = it.FromThreadId, text = it.Text },
            ThreadIdleNotification n => new { type = "thread_idle_notification", childThreadId = n.ChildThreadId, lastIntent = n.LastIntent },
            NewThreadTask t => new { type = "new_thread_task", threadId = t.ThreadId, parentThreadId = t.ParentThreadId, isFork = t.IsFork, message = t.Message },
            AssistantMessage a => new { type = "assistant_message", text = a.Text },
            AssistantTextDelta d => new { type = "assistant_text_delta", textDelta = d.TextDelta },
            ReasoningTextDelta r => new { type = "reasoning_text_delta", textDelta = r.TextDelta },
            ReasoningMessage r => new { type = "reasoning_message", text = r.Text },
            SessionTitleSet t => new { type = "session_title_set", title = t.Title },
            SetModel m => new { type = "set_model", model = m.Model },
            TurnStarted => new { type = "turn_started" },
            TurnEnded => new { type = "turn_ended" },

            TokenUsage u => new { type = "token_usage", inputTokens = u.InputTokens, outputTokens = u.OutputTokens, totalTokens = u.TotalTokens, providerModel = u.ProviderModel },

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
                kind = e.Kind.ToString(),
                meta = e.Meta,
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

            case "inter_thread_message":
                return new InterThreadMessage(
                    FromThreadId: root.GetProperty("fromThreadId").GetString() ?? string.Empty,
                    Text: root.GetProperty("text").GetString() ?? string.Empty);

            case "thread_idle_notification":
                return new ThreadIdleNotification(
                    ChildThreadId: root.GetProperty("childThreadId").GetString() ?? string.Empty,
                    LastIntent: root.GetProperty("lastIntent").GetString() ?? string.Empty);

            case "new_thread_task":
                return new NewThreadTask(
                    ThreadId: root.GetProperty("threadId").GetString() ?? string.Empty,
                    ParentThreadId: root.GetProperty("parentThreadId").GetString() ?? string.Empty,
                    IsFork: root.GetProperty("isFork").GetBoolean(),
                    Message: root.GetProperty("message").GetString() ?? string.Empty);

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

            case "set_model":
                return new SetModel(root.GetProperty("model").GetString() ?? string.Empty);

            case "turn_started":
                return new TurnStarted();

            case "turn_ended":
                return new TurnEnded();

            case "token_usage":
            {
                static long? ReadNullableLong(JsonElement root, string name)
                {
                    if (!root.TryGetProperty(name, out var el)) return null;
                    if (el.ValueKind == JsonValueKind.Null) return null;
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
                    if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var ns)) return ns;
                    return null;
                }

                static string? ReadOptionalString(JsonElement root, string name)
                {
                    if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
                    return el.GetString();
                }

                return new TokenUsage(
                    InputTokens: ReadNullableLong(root, "inputTokens"),
                    OutputTokens: ReadNullableLong(root, "outputTokens"),
                    TotalTokens: ReadNullableLong(root, "totalTokens"),
                    ProviderModel: ReadOptionalString(root, "providerModel"));
            }

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
            {
                static string GetStringOrEmpty(JsonElement root, string name)
                {
                    if (!root.TryGetProperty(name, out var el)) return string.Empty;
                    return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.ToString();
                }

                static string? GetOptionalString(JsonElement root, string name)
                {
                    if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
                    return el.GetString();
                }

                string? kindStr = null;
                var kindOk = false;
                Agent.Harness.Threads.ThreadInboxMessageKind parsed = default;

                if (root.TryGetProperty("kind", out var kindEl))
                {
                    if (kindEl.ValueKind == JsonValueKind.String)
                    {
                        kindStr = kindEl.GetString();
                        kindOk = Enum.TryParse<Agent.Harness.Threads.ThreadInboxMessageKind>(kindStr, ignoreCase: true, out parsed);
                    }
                    else if (kindEl.ValueKind == JsonValueKind.Number && kindEl.TryGetInt32(out var kindInt))
                    {
                        kindStr = kindInt.ToString();
                        if (Enum.IsDefined(typeof(Agent.Harness.Threads.ThreadInboxMessageKind), kindInt))
                        {
                            parsed = (Agent.Harness.Threads.ThreadInboxMessageKind)kindInt;
                            kindOk = true;
                        }
                    }
                    else
                    {
                        // Preserve whatever was present for diagnostics.
                        kindStr = kindEl.ToString();
                    }
                }

                var kind = kindOk ? parsed : Agent.Harness.Threads.ThreadInboxMessageKind.InterThreadMessage;

                var metaBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("meta", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in metaEl.EnumerateObject())
                        metaBuilder[p.Name] = p.Value.GetString() ?? p.Value.ToString();
                }

                // Preserve the raw kind string for forward-compat/replay debugging.
                if (!kindOk && !string.IsNullOrWhiteSpace(kindStr))
                    metaBuilder[Agent.Harness.Threads.ThreadInboxMetaKeys.UnknownInboxKind] = kindStr;

                ImmutableDictionary<string, string>? meta = metaBuilder.Count == 0 ? null : metaBuilder.ToImmutable();

                return new ThreadInboxMessageEnqueued(
                    ThreadId: GetStringOrEmpty(root, "threadId"),
                    EnvelopeId: GetStringOrEmpty(root, "envelopeId"),
                    Kind: kind,
                    Meta: meta,
                    Source: GetStringOrEmpty(root, "source"),
                    SourceThreadId: GetOptionalString(root, "sourceThreadId"),
                    Delivery: Agent.Harness.Threads.ThreadInboxDeliveryText.Normalize(GetOptionalString(root, "delivery")),
                    EnqueuedAtIso: GetStringOrEmpty(root, "enqueuedAtIso"),
                    Text: GetStringOrEmpty(root, "text"));
            }

            case "thread_inbox_message_dequeued":
                return new ThreadInboxMessageDequeued(
                    ThreadId: root.TryGetProperty("threadId", out var tid1) ? (tid1.GetString() ?? string.Empty) : string.Empty,
                    EnvelopeId: root.TryGetProperty("envelopeId", out var eid1) ? (eid1.GetString() ?? string.Empty) : string.Empty,
                    DequeuedAtIso: root.TryGetProperty("dequeuedAtIso", out var ts1) ? (ts1.GetString() ?? string.Empty) : string.Empty);

            // Back-compat: older name
            case "thread_inbox_message_drained_for_prompt":
                return new ThreadInboxMessageDequeued(
                    ThreadId: root.TryGetProperty("threadId", out var tid2) ? (tid2.GetString() ?? string.Empty) : string.Empty,
                    EnvelopeId: root.TryGetProperty("envelopeId", out var eid2) ? (eid2.GetString() ?? string.Empty) : string.Empty,
                    DequeuedAtIso: root.TryGetProperty("drainedAtIso", out var ts2) ? (ts2.GetString() ?? string.Empty) : string.Empty);

            // Back-compat: older name
            case "thread_inbox_message_delivered_to_llm":
                return new ThreadInboxMessageDequeued(
                    ThreadId: root.TryGetProperty("threadId", out var tid3) ? (tid3.GetString() ?? string.Empty) : string.Empty,
                    EnvelopeId: root.TryGetProperty("envelopeId", out var eid3) ? (eid3.GetString() ?? string.Empty) : string.Empty,
                    DequeuedAtIso: root.TryGetProperty("deliveredAtIso", out var ts3) ? (ts3.GetString() ?? string.Empty) : string.Empty);

            default:
                throw new InvalidOperationException($"unknown_event_type:{type}");
        }
    }
}
