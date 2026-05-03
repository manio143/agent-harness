using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;

public sealed partial class SessionPickerViewModel : ObservableObject
{
    public SessionPickerViewModel()
    {
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private SessionListItemViewModel? _selected;

    [ObservableProperty]
    private bool _startNewSession;

    public bool CanOpen => StartNewSession || Selected is not null;

    public event Action<string?>? OpenRequested;

    public event Action? RefreshRequested;

    public event Action? CancelRequested;

    partial void OnSelectedChanged(SessionListItemViewModel? value)
    {
        if (value is not null)
            StartNewSession = false;

        OnPropertyChanged(nameof(CanOpen));
        OpenCommand.NotifyCanExecuteChanged();
    }

    partial void OnStartNewSessionChanged(bool value)
    {
        if (value)
            Selected = null;

        OnPropertyChanged(nameof(CanOpen));
        OpenCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
        => OpenRequested?.Invoke(StartNewSession ? null : Selected?.SessionId);

    [RelayCommand]
    private void Refresh()
        => RefreshRequested?.Invoke();

    [RelayCommand]
    private void Cancel()
        => CancelRequested?.Invoke();
}
