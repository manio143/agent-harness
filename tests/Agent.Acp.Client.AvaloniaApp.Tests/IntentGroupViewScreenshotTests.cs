using System.IO;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class IntentGroupViewScreenshotTests
{
    [AvaloniaFact]
    public void Renders_expanded_and_collapsed_states()
    {
        var items = new object[]
        {
            "tool: read_file → README.md",
            "tool: grep → \"Intent\"",
            "tool: write_file → output.json"
        };

        var vm = new IntentGroupViewModel("Search repo", items);
        var view = new IntentGroupView { DataContext = vm };

        Save(view, "intent-group-expanded.png");

        vm.IsExpanded = false;
        Save(view, "intent-group-collapsed.png");
    }

    private static void Save(IntentGroupView view, string fileName)
    {
        // Size the control for a deterministic screenshot.
        view.Measure(new Size(560, 400));
        view.Arrange(new Rect(0, 0, 560, 400));
        view.UpdateLayout();

        var pixelSize = new PixelSize(560, 400);
        using var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bmp.Render(view);

        var outDir = Path.Combine(GetRepoRoot(), "docs", "ux", "screens");
        Directory.CreateDirectory(outDir);

        var outPath = Path.Combine(outDir, fileName);

        using var ms = new MemoryStream();
        bmp.Save(ms);
        File.WriteAllBytes(outPath, ms.ToArray());

        var fi = new FileInfo(outPath);
        Assert.True(fi.Exists, $"Expected screenshot to be created at {outPath}");
        Assert.True(fi.Length > 0, $"Expected screenshot to be non-empty at {outPath}");
    }

    private static string GetRepoRoot()
    {
        // Test assembly runs from bin/...; climb until we find Agent.slnx.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}
