using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;

namespace Agent.Server;

public static class MainThreadCommittedEvents
{
    /// <summary>
    /// Main thread is treated as just another thread.
    /// When available, load from the JsonlThreadStore (threads/{main}/events.jsonl).
    /// Otherwise, fall back to the legacy session-level store (events.jsonl).
    /// </summary>
    public static ImmutableArray<SessionEvent> Load(ISessionStore store, string sessionId)
    {
        if (store is JsonlSessionStore js)
        {
            var threadStore = new JsonlThreadStore(js.RootDir);
            return threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        }

        return store.LoadCommitted(sessionId);
    }
}
