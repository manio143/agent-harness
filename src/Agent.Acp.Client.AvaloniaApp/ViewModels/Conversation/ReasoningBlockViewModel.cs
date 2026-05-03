using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed partial class ReasoningBlockViewModel : ObservableObject
{
    public ReasoningBlockViewModel(string text)
    {
        Text = text;
        IsExpanded = false;
    }

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
}
