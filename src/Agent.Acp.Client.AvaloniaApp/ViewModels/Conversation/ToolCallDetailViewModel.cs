using CommunityToolkit.Mvvm.ComponentModel;

using System;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Clipboard;
using CommunityToolkit.Mvvm.Input;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ToolCallDetailViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _copyStatus;
    private readonly IClipboardService? _clipboard;

    public ToolCallDetailViewModel(string toolCallId, string title, string status, string? rawInputJson, string? rawOutputJson, IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
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

    [RelayCommand]
    private async Task CopyInputAsync()
    {
        if (_clipboard is null)
            throw new InvalidOperationException("Clipboard service not configured");

        await _clipboard.SetTextAsync(RawInputJson ?? string.Empty).ConfigureAwait(false);
        CopyStatus = "Copied input";
    }

    [RelayCommand]
    private async Task CopyOutputAsync()
    {
        if (_clipboard is null)
            throw new InvalidOperationException("Clipboard service not configured");

        await _clipboard.SetTextAsync(RawOutputJson ?? string.Empty).ConfigureAwait(false);
        CopyStatus = "Copied output";
    }
}
