using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ToolCallDetailViewModel : ObservableObject
{
    public ToolCallDetailViewModel(string toolCallId, string title, string status, string? rawInputJson, string? rawOutputJson)
    {
        ToolCallId = toolCallId;
        Title = title;
        Status = status;
        RawInputJson = rawInputJson;
        RawOutputJson = rawOutputJson;
    }

    public string ToolCallId { get; }

    public string Title { get; }

    public string Status { get; }

    public string? RawInputJson { get; }

    public string? RawOutputJson { get; }
}
