using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Protocol;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

[Collection("samples")]
public sealed class EndToEndMinimalAgentChatTests
{
    [Fact]
    public async Task Prompt_drives_session_update_notifications_into_chat_viewmodel()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var repo = GetRepoRoot();

        var exe = Path.Combine(repo, "samples", "Acp.MinimalAgent", "bin", "Release", "net8.0", "Acp.MinimalAgent");
        Assert.True(File.Exists(exe));

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        };

        await using var proc = await StdioAcpAgentProcess.StartAsync(psi, ct);
        _ = await AcpClientBootstrap.InitializeAsync(proc.Connection, ct);
        var session = await AcpClientBootstrap.NewSessionAsync(proc.Connection, repo, ct);

        var chat = new ChatViewModel();
        var pump = new AcpSessionUpdatePump(session.SessionId, chat);

        proc.Connection.NotificationReceived += n => pump.TryHandle(n);

        _ = await AcpClientBootstrap.PromptAsync(proc.Connection, session.SessionId, "hello", ct);

        // Minimal agent emits 2 agent message chunks: "hello from minimal agent" and "done".
        Assert.Contains(chat.Transcript, x => x is string s && s.Contains("hello from minimal agent", StringComparison.Ordinal));
        Assert.Contains(chat.Transcript, x => x is string s && s.Contains("done", StringComparison.Ordinal));
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}
