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

    [ObservableProperty]
    private string? _rawInputJson;

    [ObservableProperty]
    private string? _rawOutputJson;

    public string? InputPreview => Preview(RawInputJson);

    public string? OutputPreview => Preview(RawOutputJson);

    public ToolCallDetailViewModel Detail
        => new ToolCallDetailViewModel(
            toolCallId: ToolCallId,
            title: Title,
            status: Status.ToString(),
            rawInputJson: RawInputJson,
            rawOutputJson: RawOutputJson);

    private static string? Preview(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }
}
