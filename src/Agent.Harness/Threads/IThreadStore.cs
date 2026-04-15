using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public interface IThreadStore
{
    void CreateMainIfMissing(string sessionId);

    ThreadMetadata? TryLoadThreadMetadata(string sessionId, string threadId);
    ImmutableArray<ThreadMetadata> ListThreads(string sessionId);

    void CreateThread(string sessionId, ThreadMetadata metadata);
    void SaveThreadMetadata(string sessionId, ThreadMetadata metadata);

    void AppendInbox(string sessionId, string threadId, ThreadEnvelope envelope);
    ImmutableArray<ThreadEnvelope> LoadInbox(string sessionId, string threadId);
    void ClearInbox(string sessionId, string threadId);

    // Thread committed events (for thread_read)
    void AppendCommittedEvent(string sessionId, string threadId, SessionEvent evt);
    ImmutableArray<SessionEvent> LoadCommittedEvents(string sessionId, string threadId);
}
