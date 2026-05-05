using System;
using System.Collections.Generic;
using System.Linq;
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

    public string? InputPreview => PreviewInput(RawInputJson);

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

    private static string? PreviewInput(string? rawInputJson)
    {
        if (string.IsNullOrWhiteSpace(rawInputJson)) return null;
        rawInputJson = rawInputJson.Trim();

        // Try to render a human-friendly summary for small JSON objects.
        // This makes the tool row useful even when the full args are only visible in the detail flyout.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawInputJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var props = doc.RootElement.EnumerateObject().Take(6).ToList();
                if (props.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var p in props)
                    {
                        var v = p.Value;
                        var rendered = v.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => v.GetString(),
                            System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                            System.Text.Json.JsonValueKind.True => "true",
                            System.Text.Json.JsonValueKind.False => "false",
                            System.Text.Json.JsonValueKind.Array => RenderArray(v),
                            _ => null,
                        };

                        if (!string.IsNullOrEmpty(rendered))
                            parts.Add($"{p.Name}={rendered}");
                    }

                    if (parts.Count > 0)
                    {
                        var summary = string.Join(' ', parts);
                        return summary.Length <= 140 ? summary : summary[..140] + "…";
                    }
                }
            }
        }
        catch
        {
            // Ignore and fall back to raw preview.
        }

        return Preview(rawInputJson);

        static string RenderArray(System.Text.Json.JsonElement a)
        {
            try
            {
                var items = a.EnumerateArray().Take(5).Select(e =>
                {
                    return e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : e.GetRawText();
                }).Where(s => !string.IsNullOrEmpty(s)).ToList();

                var inner = string.Join(",", items);
                return $"[{inner}]";
            }
            catch
            {
                return "[...]";
            }
        }
    }

    private static string? Preview(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }
}
