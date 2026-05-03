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
        Save(view, "shell-connection.png");
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
        Save(view, "shell-chat.png");
    }

    private static void Save(ShellView view, string fileName)
    {
        view.Measure(new Size(1000, 720));
        view.Arrange(new Rect(0, 0, 1000, 720));
        view.UpdateLayout();

        var pixelSize = new PixelSize(1000, 720);
        using var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bmp.Render(view);

        var outDir = Path.Combine(GetRepoRoot(), "docs", "ux", "screens");
        Directory.CreateDirectory(outDir);

        var outPath = Path.Combine(outDir, fileName);
        using var ms = new MemoryStream();
        bmp.Save(ms);
        File.WriteAllBytes(outPath, ms.ToArray());

        var fi = new FileInfo(outPath);
        Assert.True(fi.Exists);
        Assert.True(fi.Length > 0);
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}
