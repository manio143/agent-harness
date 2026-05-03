using System.Collections.Generic;
using System.Collections.ObjectModel;
using Agent.Acp.Client.AvaloniaApp.Domain.Conversation;
using Agent.Acp.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

/// <summary>
/// Thin MVVM adapter over a pure MVU domain reducer.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly Agent.Acp.Client.AvaloniaApp.Services.Clipboard.IClipboardService? _clipboard;

    public ChatViewModel(Agent.Acp.Client.AvaloniaApp.Services.Clipboard.IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
    }

    private ChatState _state = ChatState.Empty;

    private bool _isStreaming;

    // Persist expand/collapse state across transcript rebuilds.
    private readonly Dictionary<int, bool> _thoughtExpandedById = new();
    private bool _lastThoughtExpanded = false;

    public ObservableCollection<object> Transcript { get; } = new();

    public void Apply(SessionUpdate update)
    {
        _state = ChatReducer.Reduce(_state, new ChatSessionUpdate(update));

        _isStreaming = update is AgentMessageChunk or AgentThoughtChunk;
        RebuildTranscript();
    }

    public void ApplyUserPrompt(string text)
    {
        _state = ChatReducer.Reduce(_state, new ChatUserPrompt(text));
        _isStreaming = false;
        RebuildTranscript();
    }

    public void Replay(IEnumerable<Domain.Conversation.ChatEvent> events)
    {
        _state = ChatState.Empty;
        _isStreaming = false;
        _thoughtExpandedById.Clear();
        _lastThoughtExpanded = false;

        foreach (var e in events)
        {
            _state = ChatReducer.Reduce(_state, e);
        }

        RebuildTranscript();
    }

    private void RebuildTranscript()
    {
        Transcript.Clear();

        IntentGroupViewModel? pendingGroup = null;

        foreach (var item in _state.Items)
        {
            switch (item)
            {
                case ChatToolCall tc:
                {
                    // Group consecutive tool calls with the same intent title.
                    if (pendingGroup is null || pendingGroup.Title != tc.IntentTitle)
                    {
                        pendingGroup = new IntentGroupViewModel(tc.IntentTitle, items: []);
                        Transcript.Add(pendingGroup);
                    }

                    pendingGroup.Items.Add(new ToolCallRowViewModel(tc.ToolCallId, tc.Title, clipboard: _clipboard)
                    {
                        Status = tc.Status,
                        RawInputJson = tc.RawInputJson,
                        RawOutputJson = tc.RawOutputJson,
                    });

                    break;
                }

                case ChatText t:
                    pendingGroup = null;
                    Transcript.Add(t.Text);
                    break;

                case ChatUserText u:
                    pendingGroup = null;
                    Transcript.Add(new UserMessageViewModel(u.Text));
                    break;

                case ChatThought th:
                    pendingGroup = null;

                    var initialExpanded = _thoughtExpandedById.TryGetValue(th.ThoughtId, out var expanded)
                        ? expanded
                        : _lastThoughtExpanded;

                    var reasoningVm = new ReasoningBlockViewModel(th.ThoughtId, th.Text, initialExpanded);
                    reasoningVm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(ReasoningBlockViewModel.IsExpanded))
                        {
                            _thoughtExpandedById[reasoningVm.ThoughtId] = reasoningVm.IsExpanded;
                            _lastThoughtExpanded = reasoningVm.IsExpanded;
                        }
                    };

                    _thoughtExpandedById[th.ThoughtId] = reasoningVm.IsExpanded;
                    Transcript.Add(reasoningVm);
                    break;
            }
        }

        if (_isStreaming)
        {
            var label = _state.LastChunk switch
            {
                LastChunkKind.AgentThought => "Reasoning…",
                LastChunkKind.AgentMessage => "Streaming…",
                _ => "Streaming…"
            };

            Transcript.Add(new StreamingIndicatorViewModel(label));
        }
    }

}
