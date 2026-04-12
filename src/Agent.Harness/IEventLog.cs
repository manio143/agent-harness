using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Legacy mutable log interface (will likely be removed once the reducer-driven pipeline is fully adopted).
/// Kept temporarily to avoid breaking all callers at once.
/// </summary>
public interface IEventLog
{
    void Append(SessionEvent evt);

    IReadOnlyList<SessionEvent> Events { get; }
}

public sealed class InMemoryEventLog : IEventLog
{
    private readonly List<SessionEvent> _events = new();

    public IReadOnlyList<SessionEvent> Events => _events;

    public void Append(SessionEvent evt) => _events.Add(evt);

    public ImmutableArray<SessionEvent> ToImmutable() => _events.ToImmutableArray();
}
