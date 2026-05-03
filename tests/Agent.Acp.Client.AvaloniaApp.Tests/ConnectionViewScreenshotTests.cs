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

        var view = new ConnectionView { DataContext = vm };
        Save(view, "connect-screen.png");

        vm.Error = "Command is required";
        Save(view, "connect-screen-error.png");
    }

    private static void Save(ConnectionView view, string fileName)
    {
        view.Measure(new Size(900, 520));
        view.Arrange(new Rect(0, 0, 900, 520));
        view.UpdateLayout();

        var pixelSize = new PixelSize(900, 520);
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
