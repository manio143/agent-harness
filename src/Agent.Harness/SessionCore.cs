namespace Agent.Harness;

// NOTE: This class is intentionally kept temporarily as a thin imperative helper.
// The long-term direction is: MEAI streaming -> ObservedChatEvent -> Core.Reduce -> committed SessionEvent.
//
// TDD will migrate callers off this type.

public sealed record TurnResult(string AssistantText);

public sealed class SessionCore
{
    private readonly IChatClient _chat;

    public SessionCore(IChatClient chat)
    {
        _chat = chat;
    }

    public async Task<TurnResult> HandleUserMessageAsync(SessionState state, string text, CancellationToken cancellationToken)
    {
        // TDD: implement by driving Core + chat client streaming.
        throw new NotImplementedException();
    }
}
