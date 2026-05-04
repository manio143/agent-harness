using System.Linq;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class SessionPickerViewModelTests
{
    private static SessionListItemViewModel Item(string id, string title, string? updatedAt = null)
        => new SessionListItemViewModel(id, title, updatedAt);

    [Fact]
    public void SetSessions_seeds_Sessions_and_FilteredSessions()
    {
        var vm = new SessionPickerViewModel();

        vm.SetSessions(new[]
        {
            Item("s1", "First"),
            Item("s2", "Second"),
        });

        Assert.Equal(2, vm.Sessions.Count);
        Assert.Equal(2, vm.FilteredSessions.Count);
        Assert.Equal("s1", vm.FilteredSessions[0].SessionId);
        Assert.Equal("s2", vm.FilteredSessions[1].SessionId);
    }

    [Theory]
    [InlineData("fir", "s1")]
    [InlineData("S2", "s2")]
    [InlineData("2026-05", "s2")]
    public void FilterText_filters_by_title_or_id_or_updatedAt(string query, string expectedId)
    {
        var vm = new SessionPickerViewModel();

        vm.SetSessions(new[]
        {
            Item("s1", "First session", updatedAt: "2026-04-01"),
            Item("s2", "Other", updatedAt: "2026-05-03"),
        });

        vm.FilterText = query;

        Assert.Single(vm.FilteredSessions);
        Assert.Equal(expectedId, vm.FilteredSessions[0].SessionId);
    }

    [Fact]
    public void FilterText_clears_selection_if_selected_item_is_filtered_out()
    {
        var vm = new SessionPickerViewModel();

        var s1 = Item("s1", "First");
        var s2 = Item("s2", "Second");

        vm.SetSessions(new[] { s1, s2 });
        vm.Selected = s1;

        vm.FilterText = "second";

        Assert.Null(vm.Selected);
        Assert.Single(vm.FilteredSessions);
        Assert.Equal("s2", vm.FilteredSessions[0].SessionId);
    }

    [Fact]
    public void Selecting_a_session_disables_StartNewSession_and_enables_CanOpen()
    {
        var vm = new SessionPickerViewModel();
        var s1 = Item("s1", "First");

        vm.SetSessions(new[] { s1 });
        vm.StartNewSession = true;

        vm.Selected = s1;

        Assert.False(vm.StartNewSession);
        Assert.True(vm.CanOpen);
    }

    [Fact]
    public void Enabling_StartNewSession_clears_Selected_and_enables_CanOpen()
    {
        var vm = new SessionPickerViewModel();
        var s1 = Item("s1", "First");

        vm.SetSessions(new[] { s1 });
        vm.Selected = s1;

        vm.StartNewSession = true;

        Assert.Null(vm.Selected);
        Assert.True(vm.StartNewSession);
        Assert.True(vm.CanOpen);
    }

    [Fact]
    public void CanOpen_is_false_when_no_selection_and_not_starting_new()
    {
        var vm = new SessionPickerViewModel();
        vm.SetSessions(new[] { Item("s1", "First") });

        vm.Selected = null;
        vm.StartNewSession = false;

        Assert.False(vm.CanOpen);
        Assert.False(vm.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void Open_invokes_OpenRequested_with_null_when_StartNewSession()
    {
        var vm = new SessionPickerViewModel();
        vm.SetSessions(new[] { Item("s1", "First") });
        vm.StartNewSession = true;

        string? opened = "not-set";
        vm.OpenRequested += sessionId => opened = sessionId;

        vm.OpenCommand.Execute(null);

        Assert.Null(opened);
    }

    [Fact]
    public void Open_invokes_OpenRequested_with_selected_id_when_not_StartNewSession()
    {
        var vm = new SessionPickerViewModel();
        var s1 = Item("s1", "First");
        vm.SetSessions(new[] { s1 });
        vm.Selected = s1;

        string? opened = null;
        vm.OpenRequested += sessionId => opened = sessionId;

        vm.OpenCommand.Execute(null);

        Assert.Equal("s1", opened);
    }
}
