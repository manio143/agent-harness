using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ReasoningBlockViewModel : ObservableObject
{
    public ReasoningBlockViewModel(int thoughtId, string text, bool isExpanded)
    {
        ThoughtId = thoughtId;
        Text = text;
        IsExpanded = isExpanded;
    }

    public int ThoughtId { get; }

    public string Text { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public string Preview
    {
        get
        {
            var t = Text.Trim();
            if (t.Length <= 80) return t;
            return t[..80] + "…";
        }
    }

    public string HeaderText => IsExpanded ? "Reasoning" : $"Reasoning… {Preview}";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(HeaderText));
    }
}
