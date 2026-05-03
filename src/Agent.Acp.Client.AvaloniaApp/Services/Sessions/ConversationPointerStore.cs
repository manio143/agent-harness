using System;
using System.IO;

namespace Agent.Acp.Client.AvaloniaApp.Services.Sessions;

public sealed class ConversationPointerStore
{
    private readonly string _pointerPath;

    public ConversationPointerStore(string cwd)
    {
        _pointerPath = Path.Combine(cwd, ".acp-client", "last-conversation.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(_pointerPath) ?? ".");
    }

    public string? TryRead()
    {
        if (!File.Exists(_pointerPath)) return null;
        var path = File.ReadAllText(_pointerPath).Trim();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Write(string conversationLogPath)
    {
        if (string.IsNullOrWhiteSpace(conversationLogPath))
            throw new ArgumentException("conversationLogPath is required", nameof(conversationLogPath));

        File.WriteAllText(_pointerPath, conversationLogPath);
    }
}
