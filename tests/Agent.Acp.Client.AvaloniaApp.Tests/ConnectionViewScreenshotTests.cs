using System.IO;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Agent.Acp.Client.AvaloniaApp.Views.Connection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ConnectionViewScreenshotTests
{
    [AvaloniaFact]
    public void Renders_connect_screen()
    {
        var vm = new ConnectionViewModel
        {
            Command = "dotnet",
            Arguments = "run --project src/Agent.Server",
            WorkingDirectory = "/repo",
        };

        ScreenshotTestHarness.Save(new ConnectionView { DataContext = vm }, width: 900, height: 520, fileName: "connect-screen.png");

        vm.Error = "Command is required";
        ScreenshotTestHarness.Save(new ConnectionView { DataContext = vm }, width: 900, height: 520, fileName: "connect-screen-error.png");
    }

}
