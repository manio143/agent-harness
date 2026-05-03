using Agent.Acp.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ToolCallRowViewModel : ObservableObject
{
    public ToolCallRowViewModel(string toolCallId, string title)
    {
        ToolCallId = toolCallId;
        Title = title;
    }

    public string ToolCallId { get; }

    public string Title { get; }

    [ObservableProperty]
    private ToolCallStatus _status;

    public void Apply(ToolCall call)
    {
        Status = call.Status;
    }

    public void Apply(ToolCallUpdate update)
    {
        if (update.Status != default)
            Status = update.Status;
    }
}
