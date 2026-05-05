using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Schema;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ToolCallRowViewModelTests
{
    [Fact]
    public void InputPreview_prefers_compact_kv_summary_for_small_json_objects()
    {
        var vm = new ToolCallRowViewModel("t1", "host.exec")
        {
            Status = ToolCallStatus.Pending,
            RawInputJson = "{\"cmd\":\"echo\",\"args\":[\"hi\"]}",
        };

        Assert.Equal("cmd=echo args=[hi]", vm.InputPreview);
    }

    [Fact]
    public void InputPreview_falls_back_to_trimmed_json_when_not_parseable()
    {
        var vm = new ToolCallRowViewModel("t1", "x")
        {
            Status = ToolCallStatus.Pending,
            RawInputJson = "not-json",
        };

        Assert.Equal("not-json", vm.InputPreview);
    }
}
