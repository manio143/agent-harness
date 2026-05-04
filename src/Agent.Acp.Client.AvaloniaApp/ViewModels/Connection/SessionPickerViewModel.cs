using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;

public sealed partial class SessionPickerViewModel : ObservableObject
{
    public SessionPickerViewModel()
    {
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = new();

    public ObservableCollection<SessionListItemViewModel> FilteredSessions { get; } = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private SessionListItemViewModel? _selected;

    [ObservableProperty]
    private bool _startNewSession;

    public bool CanOpen => StartNewSession || Selected is not null;

    public event Action<string?>? OpenRequested;

    public event Action? RefreshRequested;

    public event Action? DisconnectRequested;

    public event Action? CancelRequested;

    partial void OnSelectedChanged(SessionListItemViewModel? value)
    {
        if (value is not null)
            StartNewSession = false;

        OnPropertyChanged(nameof(CanOpen));
        OpenCommand.NotifyCanExecuteChanged();
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
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

    public void SetSessions(System.Collections.Generic.IEnumerable<SessionListItemViewModel> sessions)
    {
        var selectedId = Selected?.SessionId;

        Sessions.Clear();
        foreach (var s in sessions)
            Sessions.Add(s);

        // Selection retention: if the previously-selected session still exists after refresh,
        // keep it selected (unless user explicitly chose StartNewSession).
        if (!StartNewSession && !string.IsNullOrWhiteSpace(selectedId))
        {
            var match = Sessions.FirstOrDefault(s => s.SessionId == selectedId);
            if (match is not null)
                Selected = match;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = FilterText?.Trim();
        FilteredSessions.Clear();

        if (string.IsNullOrWhiteSpace(q))
        {
            foreach (var s in Sessions)
                FilteredSessions.Add(s);
            return;
        }

        foreach (var s in Sessions)
        {
            if (s.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.SessionId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.DisplayUpdatedAt?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                FilteredSessions.Add(s);
            }
        }

        // If selection is filtered out, clear selection.
        if (Selected is not null && !FilteredSessions.Contains(Selected))
            Selected = null;
    }

    [RelayCommand]
    private void Refresh()
        => RefreshRequested?.Invoke();

    [RelayCommand]
    private void Disconnect()
        => DisconnectRequested?.Invoke();

    [RelayCommand]
    private void Cancel()
        => CancelRequested?.Invoke();
}
