using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class IntentGroupViewModelTests
{
    [Fact]
    public void Ctor_sets_title_and_defaults_to_expanded()
    {
        var vm = new IntentGroupViewModel(title: "Search repo", items: []);

        Assert.Equal("Search repo", vm.Title);
        Assert.True(vm.IsExpanded);
    }

    [Fact]
    public void ToggleExpanded_flips_state()
    {
        var vm = new IntentGroupViewModel(title: "Do thing", items: []);

        vm.ToggleExpanded();
        Assert.False(vm.IsExpanded);

        vm.ToggleExpanded();
        Assert.True(vm.IsExpanded);
    }
}
