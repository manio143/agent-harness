namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed class SystemMessageViewModel
{
    public SystemMessageViewModel(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
