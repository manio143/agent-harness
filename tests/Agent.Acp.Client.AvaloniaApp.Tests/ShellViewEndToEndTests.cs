using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

[Collection("samples")]
public sealed class ShellViewEndToEndTests
{
    [Fact]
    public async Task Can_connect_to_minimal_agent_and_send_prompt()
    {
        var repo = GetRepoRoot();
        var exe = Path.Combine(repo, "samples", "Acp.MinimalAgent", "bin", "Release", "net8.0", "Acp.MinimalAgent");
        Assert.True(File.Exists(exe));

        var vm = new ShellViewModel();

        // Connect via internal event (what the ConnectionViewModel triggers).
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        };

        vm.Connection.RequestConnect(psi);

        // Wait for the async connect to complete.
        await WaitUntilAsync(() => vm.IsChat, timeoutMs: 10_000);

        vm.Composer.Text = "hello";
        await vm.Composer.SendCommand.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        // Minimal agent should have emitted at least its "done" message.
        await WaitUntilAsync(() => HasText(vm.Chat, "done"), timeoutMs: 10_000);
    }

    private static bool HasText(Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation.ChatViewModel chat, string contains)
    {
        foreach (var it in chat.Transcript)
            if (it is string s && s.Contains(contains, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!predicate())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("Condition not met within timeout");
            await Task.Delay(50);
        }
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}
