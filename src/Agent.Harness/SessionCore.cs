namespace Agent.Harness;

public sealed record TurnResult(string AssistantText);

public sealed record SessionCoreOptions(bool EmitModelInvokedEvents = false);

/// <summary>
/// Functional core for a single-session, single-threaded conversational loop.
/// Owns the truth event stream; external adapters can translate events to transports (ACP, UI, etc.).
/// </summary>
public sealed class SessionCore
{
    private readonly IEventLog _log;
    private readonly IChatClient _chat;
    private readonly SessionCoreOptions _options;

    public SessionCore(IEventLog log, IChatClient chat, SessionCoreOptions? options = null)
    {
        _log = log;
        _chat = chat;
        _options = options ?? new SessionCoreOptions();
    }

    public async Task<TurnResult> HandleUserMessageAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("User message text is required", nameof(text));

        _log.Append(new UserMessageAdded(text));

        var rendered = new List<ChatMessage>
        {
            new(ChatRole.User, text),
        };

        if (_options.EmitModelInvokedEvents)
            _log.Append(new ModelInvoked(rendered));

        var assistant = await _chat.CompleteAsync(rendered, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(assistant))
            assistant = "";

        _log.Append(new AssistantMessageAdded(assistant));

        return new TurnResult(assistant);
    }
}
