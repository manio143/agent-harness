using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public interface IThreadStore
{
    void CreateMainIfMissing(string sessionId);

    ThreadMetadata? TryLoadThreadMetadata(string sessionId, string threadId);
    ImmutableArray<ThreadMetadata> ListThreads(string sessionId);

    void CreateThread(string sessionId, ThreadMetadata metadata);
    void SaveThreadMetadata(string sessionId, ThreadMetadata metadata);

    // Thread committed events (for thread_read)
    ImmutableArray<SessionEvent> LoadCommittedEvents(string sessionId, string threadId);
}

/// <summary>
/// Append-only writer for thread committed events.
///
/// ⚠️ Invariant: committed events MUST ONLY be appended from within the reducer loop
/// (TurnRunner → sink.OnCommittedAsync). Do NOT call this from arbitrary orchestrator code.
/// If you need to record something, emit an ObservedChatEvent and let the reducer commit it.
/// </summary>
public interface IThreadCommittedEventAppender
{
    void AppendCommittedEvent(string sessionId, string threadId, SessionEvent evt);
}
