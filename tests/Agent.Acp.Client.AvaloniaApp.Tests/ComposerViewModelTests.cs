using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class ComposerViewModelTests
{
    [Fact]
    public async Task Send_clears_text_on_success()
    {
        var sent = false;
        var vm = new ComposerViewModel(send: ct => { sent = true; return Task.CompletedTask; });
        vm.Text = "hello";

        await vm.SendCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(string.Empty, vm.Text);
    }

    [Fact]
    public async Task Send_sets_error_when_not_connected()
    {
        var vm = new ComposerViewModel(send: null);
        vm.Text = "hello";

        await vm.SendCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal("Not connected", vm.Error);
    }
}
