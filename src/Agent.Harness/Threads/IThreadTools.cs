using System.Collections.Immutable;

namespace Agent.Harness.Threads;

/// <summary>
/// Narrow interface for thread-related tools (list/read/metadata) used by effect executors.
/// Under option (1), these are orchestrator-owned capabilities (not store-facing types).
/// </summary>
public interface IThreadTools
{
    ImmutableArray<ThreadInfo> List();
    ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId);
    void ReportIntent(string threadId, string intent);
}
