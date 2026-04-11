namespace Agent.Harness;

/// <summary>
/// Minimal chat abstraction for the harness functional core.
/// (We can later adapt this to Microsoft.Extensions.AI IChatClient.)
/// </summary>
public interface IChatClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> renderedMessages, CancellationToken cancellationToken);
}

public sealed class ScriptedChatClient : IChatClient
{
    private string _next = "";

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

    public ScriptedChatClient WhenCalledReturn(string assistantText)
    {
        _next = assistantText;
        return this;
    }

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> renderedMessages, CancellationToken cancellationToken)
    {
        Calls.Add(renderedMessages);
        return Task.FromResult(_next);
    }
}
