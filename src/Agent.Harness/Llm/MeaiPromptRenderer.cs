using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Agent.Harness.Threads;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Llm;

/// <summary>
/// Renders committed harness events into MEAI messages.
///
/// Tool call + tool result history must be expressed using MEAI tool/function content
/// (FunctionCallContent + FunctionResultContent).
/// </summary>
public static class MeaiPromptRenderer
{
    private static Microsoft.Extensions.AI.ChatMessage CreateWithContents(Microsoft.Extensions.AI.ChatRole role, params AIContent[] contents)
    {
        // MEAI ChatMessage ctor shapes vary slightly; we avoid depending on a specific one.
        // Create a message with empty text and then set Contents via reflection if needed.
        var msg = new Microsoft.Extensions.AI.ChatMessage(role, "");

        var prop = typeof(Microsoft.Extensions.AI.ChatMessage).GetProperty("Contents");
        if (prop is null)
            return msg;

        if (prop.CanWrite)
        {
            prop.SetValue(msg, contents.ToList());
            return msg;
        }

        // init-only: try backing field (best-effort)
        var field = typeof(Microsoft.Extensions.AI.ChatMessage).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(f => f.Name.Contains("Contents", StringComparison.OrdinalIgnoreCase));
        field?.SetValue(msg, contents.ToList());

        return msg;
    }

    public static List<Microsoft.Extensions.AI.ChatMessage> Render(SessionState state, int compactionTailMessageCount = 5, int? maxTailMessageChars = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (compactionTailMessageCount <= 0) compactionTailMessageCount = 1;

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();

        // Compaction memory is rendered as a system message. Always-injected system fragments
        // (model catalog, thread envelope, etc.) are prepended later by the executor.
        //
        // Back-compat: if older sessions still contain CompactionCommitted, prefer ThreadCompacted.
        var lastCompaction = state.Committed.OfType<ThreadCompacted>().LastOrDefault();
        if (lastCompaction is not null)
        {
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                lastCompaction.Text ?? string.Empty));

            // Tail selection: last N user/assistant messages, plus tool-call tail if tool results exist
            // after the last assistant message (no assistant follow-up).
            var messageIdx = new List<int>();
            for (var i = 0; i < state.Committed.Length; i++)
            {
                if (state.Committed[i] is UserMessage or AssistantMessage)
                    messageIdx.Add(i);
            }

            var tailStart = messageIdx.Count == 0
                ? 0
                : messageIdx[Math.Max(0, messageIdx.Count - compactionTailMessageCount)];

            var lastAssistantIdx = -1;
            for (var i = state.Committed.Length - 1; i >= 0; i--)
            {
                if (state.Committed[i] is AssistantMessage)
                {
                    lastAssistantIdx = i;
                    break;
                }
            }

            var toolTailStart = -1;
            if (lastAssistantIdx >= 0)
            {
                for (var i = lastAssistantIdx + 1; i < state.Committed.Length; i++)
                {
                    if (state.Committed[i] is ToolCallRequested or ToolCallCompleted or ToolCallFailed or ToolCallCancelled or ToolCallRejected)
                    {
                        toolTailStart = i;
                        break;
                    }
                }
            }

            if (toolTailStart >= 0)
                tailStart = Math.Min(tailStart, lastAssistantIdx);

            foreach (var evt in state.Committed.Skip(tailStart))
            {
                // The compaction memory is already injected.
                if (evt is ThreadCompacted)
                    continue;

                RenderEvent(messages, evt, json, maxTailMessageChars);
            }

            return messages;
        }

        foreach (var evt in state.Committed)
        {
            RenderEvent(messages, evt, json, maxTailMessageChars);
        }

        return messages;
    }

    private static void RenderEvent(List<Microsoft.Extensions.AI.ChatMessage> messages, SessionEvent evt, JsonSerializerOptions json, int? maxTailMessageChars)
    {
        switch (evt)
        {
            case UserMessage u:
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, Truncate(u.Text, maxTailMessageChars)));
                break;

            case AssistantMessage a:
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, Truncate(a.Text, maxTailMessageChars)));
                break;

            case InterThreadMessage it:
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"<inter_thread from=\"{it.FromThreadId}\">{it.Text}</inter_thread>"));
                break;

            case ThreadIdleNotification n:
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"<thread_idle child=\"{n.ChildThreadId}\" intent=\"{n.LastIntent}\" />"));
                break;

            case NewThreadTask t:
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, Agent.Harness.Threads.NewThreadTaskMarkup.Render(t)));
                break;

            case ToolCallRequested t:
            {
                var fc = MeaiFunctionContentFactory.CreateFunctionCall(t.ToolId, t.ToolName, t.Args);
                messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Assistant, fc));
                break;
            }

            case ToolCallUpdate u:
            {
                var payload = JsonSerializer.Serialize(new { toolId = u.ToolId, content = u.Content }, json);
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"<tool_update>{payload}</tool_update>"));
                break;
            }

            case ToolCallCompleted c:
            {
                var fr = MeaiFunctionContentFactory.CreateFunctionResult(c.ToolId, c.Result);
                messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                break;
            }

            case ToolCallFailed f:
            {
                var fr = MeaiFunctionContentFactory.CreateFunctionResult(f.ToolId, new { outcome = "failed", error = f.Error });
                messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                break;
            }

            case ToolCallRejected r:
            {
                var fr = MeaiFunctionContentFactory.CreateFunctionResult(r.ToolId, new { outcome = "rejected", reason = r.Reason, details = r.Details });
                messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                break;
            }

            case ToolCallCancelled c:
            {
                var fr = MeaiFunctionContentFactory.CreateFunctionResult(c.ToolId, new { outcome = "cancelled" });
                messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                break;
            }

            default:
                break;
        }
    }

    private static string SafeRawJson(JsonElement el)
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

    private static string Truncate(string? text, int? maxChars)
    {
        if (text is null) return string.Empty;
        if (maxChars is null || maxChars <= 0) return text;
        if (text.Length <= maxChars) return text;

        var head = text.Substring(0, maxChars.Value);
        return head + $"\n\n[TRUNCATED: original_length={text.Length}]";
    }
}
