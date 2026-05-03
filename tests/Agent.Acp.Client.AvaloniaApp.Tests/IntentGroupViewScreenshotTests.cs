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

        ScreenshotTestHarness.Save(new IntentGroupView { DataContext = vm }, width: 560, height: 400, fileName: "intent-group-expanded.png");

        vm.IsExpanded = false;
        ScreenshotTestHarness.Save(new IntentGroupView { DataContext = vm }, width: 560, height: 400, fileName: "intent-group-collapsed.png");
    }

}
