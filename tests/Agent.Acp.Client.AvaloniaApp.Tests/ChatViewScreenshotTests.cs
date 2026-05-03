using System.IO;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Agent.Acp.Schema;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ChatViewScreenshotTests
{
    [AvaloniaFact]
    public void Renders_chat_with_intent_group_and_tools()
    {
        var vm = new ChatViewModel();

        // Simulate the flow: report_intent → tool calls → assistant text
        vm.Apply(new ToolCall
        {
            ToolCallId = "t0",
            Title = "report_intent",
            RawInput = Json("{\"intent\":\"Patch text file\"}"),
            Content = [],
            Locations = [],
            Kind = ToolKind.Read,
            Status = ToolCallStatus.Completed,
            RawOutput = new object(),
        });

        vm.Apply(new ToolCall
        {
            ToolCallId = "t1",
            Title = "read_text_file",
            RawInput = Json("{\"path\":\"README.md\"}"),
            Content = [],
            Locations = [],
            Kind = ToolKind.Read,
            Status = ToolCallStatus.InProgress,
            RawOutput = new object(),
        });

        vm.Apply(new ToolCallUpdate
        {
            ToolCallId = "t1",
            Status = ToolCallStatus.Completed,
        });

        vm.Apply(new AgentMessageChunk
        {
            Content = new TextContent { Text = "Done." }
        });

        var view = new ChatView { DataContext = vm };
        Save(view, "chat-sample.png");
    }

    private static object Json(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void Save(ChatView view, string fileName)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();

        var pixelSize = new PixelSize(900, 700);
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
