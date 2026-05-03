using System.IO;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ComposerViewScreenshotTests
{
    [AvaloniaFact]
    public void Renders_composer()
    {
        var vm = new ComposerViewModel();
        vm.Text = "hello";

        ScreenshotTestHarness.Save(new ComposerView { DataContext = vm }, width: 1000, height: 160, fileName: "composer.png");

        vm.Error = "Not connected";
        ScreenshotTestHarness.Save(new ComposerView { DataContext = vm }, width: 1000, height: 160, fileName: "composer-error.png");
    }

}
