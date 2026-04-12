namespace Agent.Harness;

/// <summary>
/// Observed events are produced by the imperative shell (e.g., MEAI streaming updates)
/// and fed into the functional core reducer.
///
/// Observed events are NOT publishable. The core decides what to commit.
/// </summary>
public abstract record ObservedChatEvent
{
    /// <summary>
    /// Lossless raw attachment (e.g. MEAI ChatResponseUpdate) for debugging and provider-specific enrichments.
    /// </summary>
    public object? RawUpdate { get; init; }
}

public sealed record ObservedUserMessage(string Text) : ObservedChatEvent;

public sealed record ObservedAssistantTextDelta(string Text) : ObservedChatEvent;

/// <summary>
/// Reasoning/thought delta (e.g., MEAI TextReasoningContent).
/// </summary>
public sealed record ObservedReasoningTextDelta(string Text) : ObservedChatEvent;

public sealed record ObservedAssistantMessageCompleted(string? FinishReason = null) : ObservedChatEvent;
