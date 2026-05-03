using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using Agent.Acp.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly Dictionary<string, ToolCallRowViewModel> _toolCallsById = new();
    private readonly Dictionary<string, IntentGroupViewModel> _groupByToolCallId = new();

    private string? _currentIntent;

    public ObservableCollection<object> Transcript { get; } = new();

    public void Apply(SessionUpdate update)
    {
        switch (update)
        {
            case AgentMessageChunk msg:
            {
                if (msg.Content is TextContent t)
                {
                    Transcript.Add(t.Text);
                }
                break;
            }

            case ToolCall call:
            {
                OnToolCall(call);
                break;
            }

            case ToolCallUpdate u:
            {
                OnToolCallUpdate(u);
                break;
            }
        }
    }

    private void OnToolCall(ToolCall call)
    {
        // Special support: tool report_intent updates current intent grouping.
        if (string.Equals(call.Title, "report_intent", StringComparison.OrdinalIgnoreCase))
        {
            var intent = TryGetIntent(call.RawInput);
            if (!string.IsNullOrWhiteSpace(intent))
            {
                _currentIntent = intent;
                // Create group if needed.
                var group = new IntentGroupViewModel(title: intent, items: []);
                Transcript.Add(group);
            }

            // We intentionally do NOT add the report_intent tool call itself to the visible list.
            return;
        }

        var row = new ToolCallRowViewModel(call.ToolCallId, call.Title);
        row.Apply(call);
        _toolCallsById[call.ToolCallId] = row;

        var groupVm = GetOrCreateCurrentGroup();
        groupVm.Items.Add(row);
        _groupByToolCallId[call.ToolCallId] = groupVm;
    }

    private void OnToolCallUpdate(ToolCallUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ToolCallId))
            return;

        if (_toolCallsById.TryGetValue(update.ToolCallId, out var row))
        {
            row.Apply(update);
            return;
        }

        // If we missed the ToolCall start event, create a row lazily.
        var title = string.IsNullOrWhiteSpace(update.Title) ? update.ToolCallId : update.Title;
        var late = new ToolCallRowViewModel(update.ToolCallId, title);
        late.Apply(update);
        _toolCallsById[update.ToolCallId] = late;

        var groupVm = GetOrCreateCurrentGroup();
        groupVm.Items.Add(late);
        _groupByToolCallId[update.ToolCallId] = groupVm;
    }

    private IntentGroupViewModel GetOrCreateCurrentGroup()
    {
        if (!string.IsNullOrWhiteSpace(_currentIntent))
        {
            // Find last group in transcript matching the current intent.
            for (var i = Transcript.Count - 1; i >= 0; i--)
            {
                if (Transcript[i] is IntentGroupViewModel g && g.Title == _currentIntent)
                    return g;
            }
        }

        // Fallback group when intent isn't known.
        const string fallback = "Tools";
        for (var i = Transcript.Count - 1; i >= 0; i--)
        {
            if (Transcript[i] is IntentGroupViewModel g && g.Title == fallback)
                return g;
        }

        var groupVm = new IntentGroupViewModel(title: fallback, items: []);
        Transcript.Add(groupVm);
        return groupVm;
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
}
