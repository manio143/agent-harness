using Avalonia;
using Avalonia.Media;

namespace Agent.Acp.Client.AvaloniaApp.Services.Theme;

/// <summary>
/// Convenience accessors for theme brushes. Keeps hardcoded hex out of viewmodels.
/// Falls back to sensible defaults if resources are missing.
/// </summary>
public static class ThemeBrushes
{
    public static IBrush StatusRunning => FindBrush("StatusRunning", fallback: "#F59E0B");

    public static IBrush StatusSuccess => FindBrush("StatusSuccess", fallback: "#4ADE80");

    public static IBrush StatusError => FindBrush("StatusError", fallback: "#EF4444");

    public static IBrush StatusPending => FindBrush("StatusPending", fallback: "#6B7280");

    private static IBrush FindBrush(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, theme: null, out var value) == true && value is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.Parse(fallback));
    }
}
