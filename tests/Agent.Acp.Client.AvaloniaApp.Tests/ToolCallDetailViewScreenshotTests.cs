using System.IO;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ToolCallDetailViewScreenshotTests
{
    [AvaloniaFact]
    public void Renders_detail_panel()
    {
        var vm = new ToolCallDetailViewModel(
            toolCallId: "t1",
            title: "read_text_file",
            status: "completed",
            rawInputJson: "{\"path\":\"README.md\"}",
            rawOutputJson: "{\"text\":\"hello world\"}");

        var view = new ToolCallDetailView { DataContext = vm };
        Save(view, "tool-detail.png");
    }

    private static void Save(ToolCallDetailView view, string fileName)
    {
        view.Measure(new Size(560, 520));
        view.Arrange(new Rect(0, 0, 560, 520));
        view.UpdateLayout();

        var pixelSize = new PixelSize(560, 520);
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
