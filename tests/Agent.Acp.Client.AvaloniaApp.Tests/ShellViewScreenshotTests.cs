using System.IO;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;
using Agent.Acp.Client.AvaloniaApp.Views.Shell;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ShellViewScreenshotTests
{
    [AvaloniaFact]
    public void Renders_shell_in_connection_mode()
    {
        var vm = new ShellViewModel();
        vm.Status = "Not connected";
        vm.CurrentScreen = ShellViewModel.Screen.Connection;

        var view = new ShellView { DataContext = vm };
        ScreenshotTestHarness.Save(view, width: 1000, height: 720, fileName: "shell-connection.png");
    }

    [AvaloniaFact]
    public void Renders_shell_in_chat_mode()
    {
        var vm = new ShellViewModel();
        vm.Status = "Connected (session: s1)";
        vm.CurrentScreen = ShellViewModel.Screen.Chat;

        // Seed some transcript content.
        vm.Chat.Apply(new Agent.Acp.Schema.AgentMessageChunk
        {
            Content = new Agent.Acp.Schema.TextContent { Text = "hello" }
        });

        var view = new ShellView { DataContext = vm };
        ScreenshotTestHarness.Save(view, width: 1000, height: 720, fileName: "shell-chat.png");
    }

}
