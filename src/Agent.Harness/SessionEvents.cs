using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Stable, publishable session events ("committed" events).
/// These are the only events that downstream adapters (ACP/UI) are allowed to publish.
/// </summary>
public abstract record SessionEvent;

public sealed record UserMessage(string Text) : SessionEvent;
public sealed record AssistantMessage(string Text) : SessionEvent;

/// <summary>
/// Committed assistant text delta. Useful for streaming modes where we want to publish progress
/// without waiting for message completion.
/// </summary>
public sealed record AssistantTextDelta(string TextDelta) : SessionEvent;

/// <summary>
/// Committed reasoning/thought delta. Publishing is controlled separately from committing.
/// </summary>
public sealed record ReasoningTextDelta(string TextDelta) : SessionEvent;

/// <summary>
/// Debug/test-only committed event that records the exact messages rendered for the model.
/// Must be gated via options and disabled by default.
/// </summary>
public sealed record ModelInvoked(ImmutableArray<ChatMessage> RenderedMessages) : SessionEvent;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatMessage(ChatRole Role, string Text);
