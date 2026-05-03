using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task>? _send;

    public ComposerViewModel(Func<CancellationToken, Task>? send = null)
    {
        _send = send;
    }

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Text);

    [ObservableProperty]
    private string? _error;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSend));

    [RelayCommand]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        Error = null;

        if (!CanSend)
            return;

        if (_send is null)
        {
            Error = "Not connected";
            return;
        }

        try
        {
            IsBusy = true;
            await _send(cancellationToken).ConfigureAwait(false);
            Text = string.Empty;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
