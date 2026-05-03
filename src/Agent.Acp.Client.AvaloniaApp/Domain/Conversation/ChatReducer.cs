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
            ToolCall c => ReduceToolCall(state, c),
            ToolCallUpdate u => ReduceToolCallUpdate(state, u),
            _ => state,
        };

    private static ChatState ReduceAgentMessageChunk(ChatState state, AgentMessageChunk m)
    {
        if (m.Content is not TextContent t)
            return state;

        return state with
        {
            Items = state.Items.Add(new ChatText(t.Text))
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

            // Create a new group entry if the last item isn't already that group.
            var items = state.Items;
            if (items.Length == 0 || items[^1] is not ChatIntentGroup g || g.Title != intent)
                items = items.Add(new ChatIntentGroup(intent, ToolCalls: Array.Empty<ChatToolCall>()));

            return state with { CurrentIntent = intent, Items = items };
        }

        var tool = new ChatToolCall(
            call.ToolCallId,
            call.Title,
            Status: call.Status,
            RawInputJson: ToolJson.TryStringify(call.RawInput),
            RawOutputJson: ToolJson.TryStringify(call.RawOutput));
        var byId = state.ToolCallsById.SetItem(call.ToolCallId, tool);

        return AddToolToCurrentGroup(state with { ToolCallsById = byId }, tool);
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
            return state with { ToolCallsById = byId, Items = items };
        }

        // Late tool update: create a placeholder tool call.
        var title = string.IsNullOrWhiteSpace(update.Title) ? update.ToolCallId : update.Title;
        var toolLate = new ChatToolCall(
            update.ToolCallId,
            title,
            Status: update.Status,
            RawInputJson: ToolJson.HasMeaningful(update.RawInput) ? ToolJson.TryStringify(update.RawInput) : null,
            RawOutputJson: ToolJson.HasMeaningful(update.RawOutput) ? ToolJson.TryStringify(update.RawOutput) : null);

        var byIdLate = state.ToolCallsById.SetItem(update.ToolCallId, toolLate);
        return AddToolToCurrentGroup(state with { ToolCallsById = byIdLate }, toolLate);
    }

    private static ChatState AddToolToCurrentGroup(ChatState state, ChatToolCall tool)
    {
        var items = state.Items;

        // Find last group matching CurrentIntent.
        var groupIndex = FindTargetGroupIndex(items, state.CurrentIntent);
        if (groupIndex < 0)
        {
            // Fallback group.
            const string fallback = "Tools";
            groupIndex = FindTargetGroupIndex(items, fallback);
            if (groupIndex < 0)
            {
                items = items.Add(new ChatIntentGroup(fallback, Array.Empty<ChatToolCall>()));
                groupIndex = items.Length - 1;
            }
        }

        var group = (ChatIntentGroup)items[groupIndex];
        var newList = group.ToolCalls.Add(tool);
        items = items.SetItem(groupIndex, group with { ToolCalls = newList });

        return state with { Items = items };
    }

    private static int FindTargetGroupIndex(ImmutableArray<ChatItem> items, string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return -1;

        for (var i = items.Length - 1; i >= 0; i--)
        {
            if (items[i] is ChatIntentGroup g && g.Title == intent)
                return i;
        }

        return -1;
    }

    private static ImmutableArray<ChatItem> ReplaceTool(ImmutableArray<ChatItem> items, ChatToolCall updated)
    {
        // Replace inside any group tool list.
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is not ChatIntentGroup g)
                continue;

            var idx = IndexOfTool(g.ToolCalls, updated.ToolCallId);
            if (idx < 0) continue;

            var newList = g.ToolCalls.SetItem(idx, updated);
            items = items.SetItem(i, g with { ToolCalls = newList });
            return items;
        }

        return items;
    }

    private static int IndexOfTool(IReadOnlyList<ChatToolCall> tools, string toolCallId)
    {
        for (var i = 0; i < tools.Count; i++)
            if (tools[i].ToolCallId == toolCallId)
                return i;
        return -1;
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
