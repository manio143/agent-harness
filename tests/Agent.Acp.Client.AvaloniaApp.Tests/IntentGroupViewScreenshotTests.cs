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
        var vm = new IntentGroupViewModel("Search repo", items: []);

        vm.Items.Add(new ToolCallRowViewModel("t1", "read_file")
        {
            Status = Agent.Acp.Schema.ToolCallStatus.Completed,
            RawInputJson = "{\"path\":\"README.md\"}",
            RawOutputJson = "{\"ok\":true}",
        });

        vm.Items.Add(new ToolCallRowViewModel("t2", "grep")
        {
            Status = Agent.Acp.Schema.ToolCallStatus.Completed,
            RawInputJson = "{\"pattern\":\"Intent\"}",
            RawOutputJson = "{\"matches\":3}",
        });

        vm.Items.Add(new ToolCallRowViewModel("t3", "write_file")
        {
            Status = Agent.Acp.Schema.ToolCallStatus.InProgress,
            RawInputJson = "{\"path\":\"output.json\"}",
        });

        ScreenshotTestHarness.Save(new IntentGroupView { DataContext = vm }, width: 560, height: 400, fileName: "intent-group-expanded.png");

        vm.IsExpanded = false;
        ScreenshotTestHarness.Save(new IntentGroupView { DataContext = vm }, width: 560, height: 400, fileName: "intent-group-collapsed.png");
    }

}
