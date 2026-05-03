using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Agent.Acp.Client.AvaloniaApp.Services.Clipboard;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var cb = lifetime?.MainWindow?.Clipboard;

        if (cb is null)
            throw new InvalidOperationException("Clipboard is not available");

        var data = new Avalonia.Input.DataObject();
        data.Set(Avalonia.Input.DataFormats.Text, text);
        await cb.SetDataObjectAsync(data);
    }
}
