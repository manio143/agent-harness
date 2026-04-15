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
    void AppendCommittedEvent(string sessionId, string threadId, SessionEvent evt);
    ImmutableArray<SessionEvent> LoadCommittedEvents(string sessionId, string threadId);
}
