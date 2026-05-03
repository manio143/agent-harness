using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Schema;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public static class ChatReducer
{
    public static ChatState Reduce(ChatState state, ChatEvent e)
        => e switch
        {
            ChatSessionUpdate u => Reduce(state, u.Update),
            _ => state,
        };

    private static ChatState Reduce(ChatState state, SessionUpdate update)
        => update switch
        {
            AgentMessageChunk m => ReduceAgentMessageChunk(state, m),
            AgentThoughtChunk t => ReduceAgentThoughtChunk(state, t),
            ToolCall c => ReduceToolCall(state, c),
            ToolCallUpdate u => ReduceToolCallUpdate(state, u),
            _ => state with { LastChunk = LastChunkKind.None },
        };

    private static ChatState ReduceAgentMessageChunk(ChatState state, AgentMessageChunk m)
    {
        if (m.Content is not TextContent t)
            return state with { LastChunk = LastChunkKind.None };

        var items = state.Items;

        // Streaming: consecutive agent_message_chunk updates append to the same ChatText item.
        if (state.LastChunk == LastChunkKind.AgentMessage && items.Length > 0 && items[^1] is ChatText last)
        {
            items = items.SetItem(items.Length - 1, last with { Text = last.Text + t.Text });
        }
        else
        {
            items = items.Add(new ChatText(t.Text));
        }

        return state with
        {
            Items = items,
            LastChunk = LastChunkKind.AgentMessage
        };
    }

    private static ChatState ReduceAgentThoughtChunk(ChatState state, AgentThoughtChunk m)
    {
        if (m.Content is not TextContent t)
            return state with { LastChunk = LastChunkKind.None };

        var items = state.Items;

        // Streaming: consecutive agent_thought_chunk updates append to the same ChatThought item.
        if (state.LastChunk == LastChunkKind.AgentThought && items.Length > 0 && items[^1] is ChatThought last)
        {
            items = items.SetItem(items.Length - 1, last with { Text = last.Text + t.Text });
        }
        else
        {
            items = items.Add(new ChatThought(t.Text));
        }

        return state with
        {
            Items = items,
            LastChunk = LastChunkKind.AgentThought
        };
    }

    private static ChatState ReduceToolCall(ChatState state, ToolCall call)
    {
        // Special support: report_intent sets grouping intent.
        if (string.Equals(call.Title, "report_intent", StringComparison.OrdinalIgnoreCase))
        {
            var intent = TryGetIntent(call.RawInput);
            if (string.IsNullOrWhiteSpace(intent))
                return state;

            // Streaming/ordering requirement: don't reorder the timeline.
            // report_intent only sets the label for subsequent tool calls.
            return state with { CurrentIntent = intent, LastChunk = LastChunkKind.None };
        }

        var currentIntentTitle = state.CurrentIntent;
        if (string.IsNullOrWhiteSpace(currentIntentTitle)) currentIntentTitle = "Tools";

        var tool = new ChatToolCall(
            call.ToolCallId,
            call.Title,
            Status: call.Status,
            RawInputJson: ToolJson.TryStringify(call.RawInput),
            RawOutputJson: ToolJson.TryStringify(call.RawOutput),
            IntentTitle: currentIntentTitle);

        var byId = state.ToolCallsById.SetItem(call.ToolCallId, tool);
        var items = state.Items.Add(tool);

        return state with { ToolCallsById = byId, Items = items, LastChunk = LastChunkKind.None };
    }

    private static ChatState ReduceToolCallUpdate(ChatState state, ToolCallUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ToolCallId))
            return state;

        if (state.ToolCallsById.TryGetValue(update.ToolCallId, out var existing))
        {
            var status = update.Status != default ? update.Status : existing.Status;
            var updated = existing with
            {
                Status = status,
                RawInputJson = ToolJson.HasMeaningful(update.RawInput) ? ToolJson.TryStringify(update.RawInput) : existing.RawInputJson,
                RawOutputJson = ToolJson.HasMeaningful(update.RawOutput) ? ToolJson.TryStringify(update.RawOutput) : existing.RawOutputJson,
            };
            var byId = state.ToolCallsById.SetItem(update.ToolCallId, updated);
            var items = ReplaceTool(state.Items, updated);
            return state with { ToolCallsById = byId, Items = items, LastChunk = LastChunkKind.None };
        }

        // Late tool update: create a placeholder tool call.
        var title = string.IsNullOrWhiteSpace(update.Title) ? update.ToolCallId : update.Title;
        var intent = state.CurrentIntent;
        if (string.IsNullOrWhiteSpace(intent)) intent = "Tools";

        var toolLate = new ChatToolCall(
            update.ToolCallId,
            title,
            Status: update.Status,
            RawInputJson: ToolJson.HasMeaningful(update.RawInput) ? ToolJson.TryStringify(update.RawInput) : null,
            RawOutputJson: ToolJson.HasMeaningful(update.RawOutput) ? ToolJson.TryStringify(update.RawOutput) : null,
            IntentTitle: intent);

        var byIdLate = state.ToolCallsById.SetItem(update.ToolCallId, toolLate);
        var itemsLate = state.Items.Add(toolLate);
        return state with { ToolCallsById = byIdLate, Items = itemsLate, LastChunk = LastChunkKind.None };
    }

    private static ImmutableArray<ChatItem> ReplaceTool(ImmutableArray<ChatItem> items, ChatToolCall updated)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is ChatToolCall t && t.ToolCallId == updated.ToolCallId)
                return items.SetItem(i, updated);
        }

        return items;
    }

    private static string? TryGetIntent(object rawInput)
    {
        try
        {
            if (rawInput is JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.String)
                    return i.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static IReadOnlyList<ChatToolCall> Add(this IReadOnlyList<ChatToolCall> tools, ChatToolCall tool)
    {
        var list = tools as List<ChatToolCall> ?? new List<ChatToolCall>(tools);
        list.Add(tool);
        return list;
    }

    private static IReadOnlyList<ChatToolCall> SetItem(this IReadOnlyList<ChatToolCall> tools, int index, ChatToolCall tool)
    {
        var list = tools as List<ChatToolCall> ?? new List<ChatToolCall>(tools);
        list[index] = tool;
        return list;
    }
}
