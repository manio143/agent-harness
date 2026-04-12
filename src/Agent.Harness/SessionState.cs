using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Pure session state value. Committed events are append-only; buffers represent in-flight streaming.
/// </summary>
public sealed record SessionState(
    ImmutableArray<SessionEvent> Committed,
    TurnBuffer Buffer)
{
    public static SessionState Empty { get; } = new(ImmutableArray<SessionEvent>.Empty, TurnBuffer.Empty);
}

public sealed record TurnBuffer(
    string AssistantText,
    bool AssistantMessageOpen)
{
    public static TurnBuffer Empty { get; } = new("", AssistantMessageOpen: false);
}

public sealed record ReduceResult(
    SessionState Next,
    ImmutableArray<SessionEvent> NewlyCommitted);
