using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Pure session state value. Committed events are append-only; buffers represent in-flight streaming.
/// </summary>
public sealed record SessionState(
    ImmutableArray<SessionEvent> Committed,
    TurnBuffer Buffer,
    ImmutableArray<ToolDefinition> Tools)
{
    public static SessionState Empty { get; } = new(
        ImmutableArray<SessionEvent>.Empty,
        TurnBuffer.Empty,
        ImmutableArray<ToolDefinition>.Empty);
}

public sealed record TurnBuffer(
    string AssistantText,
    bool AssistantMessageOpen,
    string ReasoningText,
    bool ReasoningMessageOpen,
    bool IntentReportedThisTurn,
    bool TurnStartedFromIdle,
    bool CompactionDue = false,
    bool ContinuationPending = false,
    bool CompactionSuppressedThisTurn = false)
{
    public static TurnBuffer Empty { get; } = new(
        AssistantText: "",
        AssistantMessageOpen: false,
        ReasoningText: "",
        ReasoningMessageOpen: false,
        // Default true so unit tests that call Core.Reduce directly (without ObservedTurnStarted)
        // do not accidentally trip the per-turn intent policy.
        IntentReportedThisTurn: true,
        TurnStartedFromIdle: false,
        CompactionDue: false,
        ContinuationPending: false,
        CompactionSuppressedThisTurn: false);
}

public sealed record ReduceResult(
    SessionState Next,
    ImmutableArray<SessionEvent> NewlyCommitted,
    ImmutableArray<Effect> Effects);  // Effects emitted by reducer for SessionRunner to execute
