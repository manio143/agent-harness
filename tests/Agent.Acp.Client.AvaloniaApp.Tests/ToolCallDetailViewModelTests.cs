using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Tests.Fakes;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ToolCallDetailViewModelTests
{
    [Fact]
    public async Task CopyInput_writes_raw_input_to_clipboard()
    {
        var clip = new FakeClipboardService();
        var vm = new ToolCallDetailViewModel(
            toolCallId: "t1",
            title: "read_text_file",
            status: "completed",
            rawInputJson: "{\"path\":\"README.md\"}",
            rawOutputJson: null,
            clipboard: clip);

        await vm.CopyInputCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal("{\"path\":\"README.md\"}", clip.LastText);
    }

    [Fact]
    public async Task CopyOutput_writes_raw_output_to_clipboard()
    {
        var clip = new FakeClipboardService();
        var vm = new ToolCallDetailViewModel(
            toolCallId: "t1",
            title: "read_text_file",
            status: "completed",
            rawInputJson: null,
            rawOutputJson: "{\"text\":\"hi\"}",
            clipboard: clip);

        await vm.CopyOutputCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal("{\"text\":\"hi\"}", clip.LastText);
    }
}
