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
    bool ReasoningMessageOpen)
{
    public static TurnBuffer Empty { get; } = new(
        AssistantText: "",
        AssistantMessageOpen: false,
        ReasoningText: "",
        ReasoningMessageOpen: false);
}

public sealed record ReduceResult(
    SessionState Next,
    ImmutableArray<SessionEvent> NewlyCommitted,
    ImmutableArray<Effect> Effects);  // Effects emitted by reducer for SessionRunner to execute
