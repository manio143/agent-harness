using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

internal static class ScreenshotTestHarness
{
    public static void Save(Control content, int width, int height, string fileName)
    {
        // Important: RenderTargetBitmap.Render(control) will produce a blank/transparent image
        // if the control isn't attached to a visual root. We render a Window instead.
        var window = new Window
        {
            Width = width,
            Height = height,
            Background = Brushes.Black,
            Content = new Border
            {
                Background = Brushes.Black,
                Child = content,
            },
        };

        // Attach to a visual root. Without showing the window, RenderTargetBitmap renders fully transparent.
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Force layout.
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var pixelSize = new PixelSize(width, height);
        using var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bmp.Render(window);

        window.Close();
        Dispatcher.UIThread.RunJobs();

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
