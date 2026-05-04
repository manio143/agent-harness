using System;
using System.Linq;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;
using Agent.Acp.Client.AvaloniaApp.Views.Connection;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ShellViewVisibilityTests
{
    [AvaloniaFact]
    public void ShellView_switches_visible_screen_when_shell_viewmodel_currentscreen_changes()
    {
        var vm = new ShellViewModel();
        var view = new ShellView { DataContext = vm };

        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ForceLayout(window);

        var connection = view.GetVisualDescendants().OfType<ConnectionView>().Single();
        var picker = view.GetVisualDescendants().OfType<SessionPickerView>().Single();
        var chat = view.GetVisualDescendants().OfType<ChatView>().Single();

        // Default should be connection screen.
        Assert.True(vm.IsConnection);
        Assert.True(connection.IsEffectivelyVisible);
        Assert.False(picker.IsEffectivelyVisible);
        Assert.False(chat.IsEffectivelyVisible);

        vm.CurrentScreen = ShellViewModel.Screen.SessionPicker;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsSessionPicker);
        Assert.False(connection.IsEffectivelyVisible);
        Assert.True(picker.IsEffectivelyVisible);
        Assert.False(chat.IsEffectivelyVisible);

        vm.CurrentScreen = ShellViewModel.Screen.Chat;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsChat);
        Assert.False(connection.IsEffectivelyVisible);
        Assert.False(picker.IsEffectivelyVisible);
        Assert.True(chat.IsEffectivelyVisible);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ForceLayout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }
}
