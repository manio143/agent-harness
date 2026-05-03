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
    private ChatState _state = ChatState.Empty;

    public ObservableCollection<object> Transcript { get; } = new();

    public void Apply(SessionUpdate update)
    {
        _state = ChatReducer.Reduce(_state, new ChatSessionUpdate(update));
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

                    pendingGroup.Items.Add(new ToolCallRowViewModel(tc.ToolCallId, tc.Title)
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

                case ChatThought th:
                    pendingGroup = null;
                    Transcript.Add(new ReasoningBlockViewModel(th.Text));
                    break;
            }
        }
    }

}
