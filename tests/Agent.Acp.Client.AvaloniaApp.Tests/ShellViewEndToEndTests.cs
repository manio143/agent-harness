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
        var (repo, exe) = GetMinimalAgent();

        var vm = new ShellViewModel();

        // Connect via internal event (what the ConnectionViewModel triggers).
        vm.Connection.RequestConnect(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        });

        // Wait for the async connect to complete.
        await WaitUntilAsync(() => vm.IsChat, timeoutMs: 10_000);

        vm.Composer.Text = "hello";
        await vm.Composer.SendCommand.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        // Minimal agent should have emitted at least its "done" message.
        await WaitUntilAsync(() => HasText(vm.Chat, "done"), timeoutMs: 10_000);
    }

    [Fact]
    public async Task Connect_with_continue_last_session_enabled_and_no_sessions_still_opens_new_session()
    {
        var (repo, exe) = GetMinimalAgent();

        var vm = new ShellViewModel();
        vm.Connection.ContinueLastSession = true;

        vm.Connection.RequestConnect(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        });

        // Minimal agent returns empty session/list, so we should still land in chat via new session.
        await WaitUntilAsync(() => vm.IsChat, timeoutMs: 10_000);
        Assert.Contains("Connected (session:", vm.Status);
    }

    [Fact]
    public async Task Disconnect_returns_to_connection_screen_and_clears_session()
    {
        var (repo, exe) = GetMinimalAgent();

        var vm = new ShellViewModel();

        vm.Connection.RequestConnect(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        });

        await WaitUntilAsync(() => vm.IsChat, timeoutMs: 10_000);
        Assert.True(vm.DisconnectCommand.CanExecute(null));

        await vm.DisconnectCommand.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.True(vm.IsConnection);
        Assert.Equal("Agent: not running", vm.ConnectionSummary);
    }

    [Fact]
    public async Task Connect_with_continue_last_session_and_existing_sessions_shows_picker_and_allows_back_then_open()
    {
        var (repo, exe) = GetSessionListAgent();

        var vm = new ShellViewModel();
        vm.Connection.ContinueLastSession = true;

        vm.Connection.RequestConnect(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        });

        await WaitUntilAsync(() => vm.IsSessionPicker || vm.IsChat || vm.Connection.Error is not null, timeoutMs: 10_000);

        Assert.True(vm.IsSessionPicker, $"Expected SessionPicker but got screen={vm.CurrentScreen} status={vm.Status} error={vm.Connection.Error}");
        Assert.Equal("Agent: running (no session)", vm.ConnectionSummary);

        // Back to connection screen (agent still running)
        vm.SessionPicker.CancelCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsConnection, timeoutMs: 10_000);
        Assert.Equal("Agent: running (no session)", vm.ConnectionSummary);

        // Re-open picker by reconnecting (simple deterministic path for now).
        vm.Connection.RequestConnect(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        });

        await WaitUntilAsync(() => vm.IsSessionPicker, timeoutMs: 10_000);

        // Open existing session
        vm.SessionPicker.Selected = vm.SessionPicker.Sessions[0];
        vm.SessionPicker.OpenCommand.Execute(null);

        await WaitUntilAsync(() => vm.IsChat, timeoutMs: 10_000);
        Assert.Contains("Connected (session:", vm.Status);
    }

    [Fact]
    public async Task SessionPicker_refresh_failure_sets_status_and_keeps_picker_open()
    {
        var (repo, exe) = GetSessionListFlakyAgent();

        var vm = new ShellViewModel();
        vm.Connection.ContinueLastSession = true;

        vm.Connection.RequestConnect(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        });

        await WaitUntilAsync(() => vm.IsSessionPicker, timeoutMs: 10_000);

        vm.SessionPicker.RefreshCommand.Execute(null);

        await WaitUntilAsync(() => (vm.Status ?? string.Empty).StartsWith("Failed to fetch sessions:", StringComparison.Ordinal), timeoutMs: 10_000);
        Assert.True(vm.IsSessionPicker);
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

    private static (string repo, string exe) GetMinimalAgent()
    {
        var repo = GetRepoRoot();
        var exe = Path.Combine(repo, "samples", "Acp.MinimalAgent", "bin", "Release", "net8.0", "Acp.MinimalAgent");
        Assert.True(File.Exists(exe));
        return (repo, exe);
    }

    private static (string repo, string exe) GetSessionListAgent()
    {
        var repo = GetRepoRoot();
        var exe = Path.Combine(repo, "samples", "Acp.SessionListAgent", "bin", "Release", "net8.0", "Acp.SessionListAgent");
        Assert.True(File.Exists(exe));
        return (repo, exe);
    }

    private static (string repo, string exe) GetSessionListFlakyAgent()
    {
        var repo = GetRepoRoot();
        var exe = Path.Combine(repo, "samples", "Acp.SessionListFlakyAgent", "bin", "Release", "net8.0", "Acp.SessionListFlakyAgent");
        Assert.True(File.Exists(exe));
        return (repo, exe);
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}
