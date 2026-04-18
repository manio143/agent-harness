using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public interface IThreadLifecycle
{
    Task RequestForkChildThreadAsync(
        string parentThreadId,
        string childThreadId,
        ImmutableArray<SessionEvent> seedCommitted,
        CancellationToken cancellationToken = default);
}
