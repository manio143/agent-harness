using System.Threading.Tasks;

namespace Agent.Acp.Client.AvaloniaApp.Services.Clipboard;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}
