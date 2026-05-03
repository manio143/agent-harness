namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;

public sealed class StreamingIndicatorViewModel
{
    public StreamingIndicatorViewModel(string label)
    {
        Label = label;
    }

    public string Label { get; }
}
