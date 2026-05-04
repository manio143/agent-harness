using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;
using Agent.Acp.Client.AvaloniaApp.Views.Connection;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

[Collection("samples")]
public sealed class ShellViewUiEndToEndTests
{
    [AvaloniaFact]
    public async Task Connect_and_open_new_session_navigates_to_chat_view()
    {
        var (repo, exe) = GetMinimalAgent();

        var vm = new ShellViewModel();
        vm.Connection.Command = exe;
        vm.Connection.Arguments = "";
        vm.Connection.WorkingDirectory = repo;
        vm.Connection.ContinueLastSession = false;

        var shellView = new ShellView { DataContext = vm };

        var window = new Window { Width = 900, Height = 700, Content = shellView };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var connectionView = shellView.GetVisualDescendants().OfType<Agent.Acp.Client.AvaloniaApp.Views.Connection.ConnectionView>().First();
        var connectButton = FindByName<Button>(connectionView, "ConnectButton");
        Assert.NotNull(connectButton);
        Assert.NotNull(connectButton!.Command);

        connectButton.Command!.Execute(null);

        await WaitUntilUiAsync(() => vm.IsChat, timeoutMs: 10_000);
        Assert.True(vm.IsChat);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Connect_with_continue_last_session_shows_picker_and_open_navigates_to_chat_view()
    {
        var (repo, exe) = GetSessionListAgent();

        var vm = new ShellViewModel();
        vm.Connection.Command = exe;
        vm.Connection.Arguments = "";
        vm.Connection.WorkingDirectory = repo;
        vm.Connection.ContinueLastSession = true;

        var shellView = new ShellView { DataContext = vm };

        var window = new Window { Width = 900, Height = 700, Content = shellView };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var connectionView = shellView.GetVisualDescendants().OfType<Agent.Acp.Client.AvaloniaApp.Views.Connection.ConnectionView>().First();
        var connectButton = FindByName<Button>(connectionView, "ConnectButton");
        connectButton!.Command!.Execute(null);

        // Wait for picker screen.
        await WaitUntilUiAsync(() => vm.IsSessionPicker, timeoutMs: 10_000);
        Assert.True(vm.IsSessionPicker);

        // Select and open (via UI button).
        Assert.NotEmpty(vm.SessionPicker.Sessions);
        vm.SessionPicker.Selected = vm.SessionPicker.Sessions[0];

        var pickerView = shellView.GetVisualDescendants().OfType<SessionPickerView>().First();
        var openButton = FindByName<Button>(pickerView, "OpenButton");
        Assert.NotNull(openButton);
        Assert.NotNull(openButton!.Command);

        openButton.Command!.Execute(null);

        await WaitUntilUiAsync(() => vm.IsChat, timeoutMs: 10_000);
        Assert.True(vm.IsChat);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilUiAsync(Func<bool> predicate, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!predicate())
        {
            Dispatcher.UIThread.RunJobs();

            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("Condition not met within timeout");

            await Task.Delay(50);
        }
    }

    private static void ForceLayout(Window window)
    {
        window.Measure(new Avalonia.Size(window.Width, window.Height));
        window.Arrange(new Avalonia.Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static T? FindByName<T>(Control root, string name) where T : Control
    {
        return root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
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

    private static string GetRepoRoot()
    {
        // Walk up until we find Agent.slnx.
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repo root (Agent.slnx).");
    }
}
