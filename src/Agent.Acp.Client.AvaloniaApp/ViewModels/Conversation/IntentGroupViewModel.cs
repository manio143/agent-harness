using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class IntentGroupViewModel : ObservableObject
{
    public IntentGroupViewModel(string title, IReadOnlyList<object> items)
    {
        Title = title;
        Items = new ObservableCollection<object>(items);
        IsExpanded = true;
    }

    public string Title { get; }

    public ObservableCollection<object> Items { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public void ToggleExpanded() => IsExpanded = !IsExpanded;
}
