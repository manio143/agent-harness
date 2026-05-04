using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;
using Agent.Acp.Client.AvaloniaApp.Views.Connection;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

[Collection("samples")]
public sealed class ShellViewInteractionTests
{
    [AvaloniaFact]
    public void SessionPickerView_declares_enter_and_escape_keybindings_bound_to_vm_commands()
    {
        var vm = new SessionPickerViewModel();
        vm.SetSessions(new[]
        {
            new SessionListItemViewModel(sessionId: "s1", title: "First", updatedAt: null),
        });
        vm.Selected = vm.Sessions.Single();

        var view = new SessionPickerView { DataContext = vm };

        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var bindings = view.KeyBindings.OfType<KeyBinding>().ToList();
        Assert.NotEmpty(bindings);

        var enter = bindings.SingleOrDefault(b => b.Gesture is KeyGesture g && g.Key == Key.Enter);
        var esc = bindings.SingleOrDefault(b => b.Gesture is KeyGesture g && g.Key == Key.Escape);

        Assert.NotNull(enter);
        Assert.NotNull(esc);

        // Binding should resolve to the VM commands.
        Assert.Same(vm.OpenCommand, enter!.Command);
        Assert.Same(vm.CancelCommand, esc!.Command);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ToolCallDetailView_escape_marks_event_handled()
    {
        var vm = new Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation.ToolCallDetailViewModel(
            toolCallId: "t1",
            title: "host.exec",
            status: Agent.Acp.Schema.ToolCallStatus.Completed,
            rawInputJson: "{}",
            rawOutputJson: "{}",
            clipboard: null);

        var detail = new ToolCallDetailView { DataContext = vm };

        var window = new Window { Width = 800, Height = 600, Content = detail };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
        };

        detail.RaiseEvent(args);

        Assert.True(args.Handled);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ConnectionView_click_connect_wires_to_shell_and_navigates_to_chat()
    {
        var (repo, exe) = GetMinimalAgent();

        var vm = new ShellViewModel();
        var view = new ShellView { DataContext = vm };

        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var cmd = FindByName<TextBox>(view, "CommandTextBox");
        var args = FindByName<TextBox>(view, "ArgumentsTextBox");
        var wd = FindByName<TextBox>(view, "WorkingDirectoryTextBox");
        var connect = FindByName<Button>(view, "ConnectButton");

        Assert.NotNull(cmd);
        Assert.NotNull(args);
        Assert.NotNull(wd);
        Assert.NotNull(connect);

        // NOTE: Avalonia TextBox source updates can be focus/trigger-dependent; to keep this test
        // deterministic (interaction wiring only), seed the VM directly.
        vm.Connection.Command = exe;
        vm.Connection.Arguments = string.Empty;
        vm.Connection.WorkingDirectory = repo;

        // Still ensure controls exist (XAML wiring), but don't rely on binding update semantics.
        cmd!.Text = exe;
        args!.Text = string.Empty;
        wd!.Text = repo;

        // Execute via the Button's bound Command (exercise XAML command binding).
        Assert.NotNull(connect!.Command);
        connect.Command!.Execute(null);

        await WaitUntilAsync(() => vm.IsChat, timeoutMs: 10_000);
        ForceLayout(window);
        Assert.Contains("Connected (session:", vm.Status);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Composer_ctrl_enter_executes_send_command_and_marks_event_handled()
    {
        var invoked = false;

        var vm = new Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation.ComposerViewModel(send: _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        vm.Text = "hello";

        var view = new ComposerView { DataContext = vm };

        var window = new Window { Width = 600, Height = 200, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var composerBox = FindByName<TextBox>(view, "ComposerTextBox");
        Assert.NotNull(composerBox);

        var keyArgs = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.Control,
        };

        var onKeyDown = typeof(ComposerView).GetMethod("OnComposerKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onKeyDown);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            onKeyDown!.Invoke(view, new object?[] { composerBox, keyArgs });
        });

        Assert.True(invoked);
        Assert.True(keyArgs.Handled);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!predicate())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("Condition not met within timeout");

            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void ForceLayout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T? FindByName<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);


    private static (string repo, string exe) GetMinimalAgent()
    {
        var repo = GetRepoRoot();
        var exe = Path.Combine(repo, "samples", "Acp.MinimalAgent", "bin", "Release", "net8.0", "Acp.MinimalAgent");
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
