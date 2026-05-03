using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class IntentGroupViewModel : ObservableObject
{
    public IntentGroupViewModel(string title, IReadOnlyList<object> items)
    {
        Title = title;
        Items = new ObservableCollection<object>(items);
        Items.CollectionChanged += OnItemsCollectionChanged;

        foreach (var tool in Items.OfType<ToolCallRowViewModel>())
        {
            tool.PropertyChanged += OnToolRowPropertyChanged;
        }

        IsExpanded = true;
    }

    public string Title { get; }

    public ObservableCollection<object> Items { get; }

    /// <summary>
    /// If true, the group auto-collapses when all tool calls are in a terminal state (completed/failed).
    /// Default: true.
    /// </summary>
    public bool AutoCollapseOnTerminal { get; set; } = false;

    [ObservableProperty]
    private bool _isExpanded;

    public bool AllTerminal
    {
        get
        {
            var tools = Items.OfType<ToolCallRowViewModel>().ToArray();
            if (tools.Length == 0) return false;

            return tools.All(t => IsTerminal(t.Status));
        }
    }

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ToolCallRowViewModel>())
            {
                item.PropertyChanged += OnToolRowPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<ToolCallRowViewModel>())
            {
                item.PropertyChanged -= OnToolRowPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(AllTerminal));
        MaybeAutoCollapse();
    }

    private void OnToolRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallRowViewModel.Status))
        {
            OnPropertyChanged(nameof(AllTerminal));
            MaybeAutoCollapse();
        }
    }

    private void MaybeAutoCollapse()
    {
        if (!AutoCollapseOnTerminal) return;
        if (!IsExpanded) return;

        if (AllTerminal)
        {
            IsExpanded = false;
        }
    }

    private static bool IsTerminal(Agent.Acp.Schema.ToolCallStatus status)
    {
        var s = status.ToString();
        return s is Agent.Acp.Schema.ToolCallStatus.Completed or Agent.Acp.Schema.ToolCallStatus.Failed;
    }
}

