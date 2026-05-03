using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Schema;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class IntentGroupViewModelTests
{
    [Fact]
    public void Ctor_DefaultsToExpanded()
    {
        var vm = new IntentGroupViewModel("Search repo", items: []);
        Assert.True(vm.IsExpanded);
        Assert.False(vm.AllTerminal);
    }

    [Fact]
    public void AutoCollapseOnTerminal_WhenAllToolsTerminal_Collapses()
    {
        var t1 = new ToolCallRowViewModel("t1", "read_file") { Status = ToolCallStatus.InProgress };
        var t2 = new ToolCallRowViewModel("t2", "search") { Status = ToolCallStatus.Pending };

        var vm = new IntentGroupViewModel("Intent", items: [t1, t2])
        {
            AutoCollapseOnTerminal = true
        };

        Assert.True(vm.IsExpanded);
        Assert.False(vm.AllTerminal);

        t1.Status = ToolCallStatus.Completed;
        Assert.True(vm.IsExpanded);
        Assert.False(vm.AllTerminal);

        t2.Status = ToolCallStatus.Failed;

        Assert.True(vm.AllTerminal);
        Assert.False(vm.IsExpanded);
    }

    [Fact]
    public void AutoCollapseOnTerminal_Disabled_DoesNotCollapse()
    {
        var t1 = new ToolCallRowViewModel("t1", "read_file") { Status = ToolCallStatus.Completed };
        var t2 = new ToolCallRowViewModel("t2", "search") { Status = ToolCallStatus.Failed };

        var vm = new IntentGroupViewModel("Intent", items: [t1, t2])
        {
            AutoCollapseOnTerminal = false
        };

        Assert.True(vm.AllTerminal);
        Assert.True(vm.IsExpanded);
    }
}
