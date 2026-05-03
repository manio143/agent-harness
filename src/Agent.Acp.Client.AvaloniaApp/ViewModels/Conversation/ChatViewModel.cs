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

        foreach (var item in _state.Items)
        {
            switch (item)
            {
                case ChatText t:
                    Transcript.Add(t.Text);
                    break;

                case ChatIntentGroup g:
                {
                    var groupVm = new IntentGroupViewModel(g.Title, items: []);
                    foreach (var tc in g.ToolCalls)
                    {
                        var row = new ToolCallRowViewModel(tc.ToolCallId, tc.Title)
                        {
                            Status = tc.Status,
                            RawInputJson = tc.RawInputJson,
                            RawOutputJson = tc.RawOutputJson,
                        };
                        groupVm.Items.Add(row);
                    }
                    Transcript.Add(groupVm);
                    break;
                }
            }
        }
    }

}
