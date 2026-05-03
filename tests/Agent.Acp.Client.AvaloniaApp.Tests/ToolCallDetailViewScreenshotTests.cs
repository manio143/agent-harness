using System.IO;
using Agent.Acp.Client.AvaloniaApp.Tests.Fakes;
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
            rawOutputJson: "{\"text\":\"hello world\"}",
            clipboard: new FakeClipboardService());

        var view = new ToolCallDetailView { DataContext = vm };
        ScreenshotTestHarness.Save(view, width: 560, height: 520, fileName: "tool-detail.png");
    }

}
