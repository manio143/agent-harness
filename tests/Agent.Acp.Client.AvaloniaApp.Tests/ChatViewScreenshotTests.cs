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

        vm.Apply(new AgentThoughtChunk
        {
            Content = new TextContent { Text = "Thinking: verify output, then respond." }
        });

        // Simulate streaming chunks.
        vm.Apply(new AgentMessageChunk
        {
            Content = new TextContent { Text = "Do" }
        });

        vm.Apply(new AgentMessageChunk
        {
            Content = new TextContent { Text = "ne." }
        });

        var view = new ChatView { DataContext = vm };
        ScreenshotTestHarness.Save(view, width: 900, height: 700, fileName: "chat-sample.png");
    }

    private static object Json(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

}
