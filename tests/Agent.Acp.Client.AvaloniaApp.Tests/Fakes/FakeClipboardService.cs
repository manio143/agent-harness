using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Clipboard;

namespace Agent.Acp.Client.AvaloniaApp.Tests.Fakes;

public sealed class FakeClipboardService : IClipboardService
{
    public string? LastText { get; private set; }

    public Task SetTextAsync(string text)
    {
        LastText = text;
        return Task.CompletedTask;
    }
}
