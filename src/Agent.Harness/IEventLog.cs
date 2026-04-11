namespace Agent.Harness;

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
}
