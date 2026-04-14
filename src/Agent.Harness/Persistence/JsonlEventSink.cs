using Agent.Harness.Persistence;

namespace Agent.Harness.Persistence;

/// <summary>
/// Default production sink:
/// - Always persists committed events via <see cref="ISessionStore"/>.
/// - Optionally logs observed events to {root}/{sessionId}/observed.jsonl (diagnostic only).
/// </summary>
public sealed class JsonlEventSink : IEventSink
{
    private readonly string _sessionId;
    private readonly ISessionStore _store;
    private readonly bool _logObserved;

    public JsonlEventSink(string sessionId, ISessionStore store, bool logObserved)
    {
        _sessionId = sessionId;
        _store = store;
        _logObserved = logObserved;
    }

    public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default)
    {
        if (!_logObserved)
            return ValueTask.CompletedTask;

        if (_store is not JsonlSessionStore jsonl)
            return ValueTask.CompletedTask;

        var path = Path.Combine(jsonl.RootDir, _sessionId, "observed.jsonl");
        File.AppendAllText(path, ObservedEventJson.ToJsonl(observed) + "\n");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default)
    {
        _store.AppendCommitted(_sessionId, committed);
        return ValueTask.CompletedTask;
    }
}
