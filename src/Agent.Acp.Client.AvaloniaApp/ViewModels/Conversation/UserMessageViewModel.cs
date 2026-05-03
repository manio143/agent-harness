namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed class UserMessageViewModel
{
    public UserMessageViewModel(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
