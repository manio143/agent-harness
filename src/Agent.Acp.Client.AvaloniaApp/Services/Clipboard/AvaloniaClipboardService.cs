using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace Agent.Acp.Client.AvaloniaApp.Services.Clipboard;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var cb = lifetime?.MainWindow?.Clipboard;

        if (cb is null)
            throw new InvalidOperationException("Clipboard is not available");

        await cb.SetTextAsync(text);
    }
}
