using Agent.Acp.Schema;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ToolCallRowViewModel : ObservableObject
{
    // Status dot colors (Opus UX review)
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#F59E0B")); // amber
    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#4ADE80")); // green
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#EF4444")); // red
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#6B7280")); // gray

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

    /// <summary>Status dot color: amber=running, green=completed, red=error.</summary>
    public IBrush StatusColor
    {
        get
        {
            var s = Status.ToString().ToLowerInvariant();
            return s switch
            {
                "running" => RunningBrush,
                "completed" => CompletedBrush,
                "error" or "failed" => ErrorBrush,
                _ => DefaultBrush
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
            rawOutputJson: RawOutputJson);

    // NOTE: clipboard wiring will be injected from Shell/runtime later.

    private static string? Preview(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }
}
