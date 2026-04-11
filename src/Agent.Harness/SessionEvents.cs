namespace Agent.Harness;

/// <summary>
/// Append-only typed event stream used as the primary truth source for a session.
/// </summary>
public abstract record SessionEvent;

public sealed record UserMessageAdded(string Text) : SessionEvent;
public sealed record AssistantMessageAdded(string Text) : SessionEvent;

/// <summary>
/// Debug/test-only event that records the exact prompt messages sent to the model.
/// This event MUST be gated behind configuration so it is not emitted in production by default.
/// </summary>
public sealed record ModelInvoked(IReadOnlyList<ChatMessage> RenderedMessages) : SessionEvent;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatMessage(ChatRole Role, string Text);
