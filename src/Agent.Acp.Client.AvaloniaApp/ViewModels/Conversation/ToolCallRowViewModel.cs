using Agent.Acp.Client.AvaloniaApp.Services.Theme;
using Agent.Acp.Schema;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ToolCallRowViewModel : ObservableObject
{
    private readonly Agent.Acp.Client.AvaloniaApp.Services.Clipboard.IClipboardService? _clipboard;

    public ToolCallRowViewModel(string toolCallId, string title, Agent.Acp.Client.AvaloniaApp.Services.Clipboard.IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
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

    /// <summary>Status dot color: amber=running, green=completed, red=error.</summary>
    public IBrush StatusColor
    {
        get
        {
            var s = Status.ToString().ToLowerInvariant();
            return s switch
            {
                "running" => ThemeBrushes.StatusRunning,
                "completed" => ThemeBrushes.StatusSuccess,
                "error" or "failed" => ThemeBrushes.StatusError,
                _ => ThemeBrushes.StatusPending
            };
        }
    }

    partial void OnStatusChanged(ToolCallStatus value)
    {
        OnPropertyChanged(nameof(StatusColor));
    }

    public ToolCallDetailViewModel Detail
        => new ToolCallDetailViewModel(
            toolCallId: ToolCallId,
            title: Title,
            status: Status.ToString(),
            rawInputJson: RawInputJson,
            rawOutputJson: RawOutputJson,
            clipboard: _clipboard);

    private static string? Preview(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }
}
