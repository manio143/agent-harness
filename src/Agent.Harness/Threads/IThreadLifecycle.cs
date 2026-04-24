using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public interface IThreadLifecycle
{
    Task RequestForkChildThreadAsync(
        string parentThreadId,
        string childThreadId,
        ThreadMode mode,
        ImmutableArray<SessionEvent> seedCommitted,
        ThreadCapabilitiesSpec? capabilities,
        CancellationToken cancellationToken = default);

    Task RequestStopThreadAsync(
        string threadId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task RequestSetThreadModelAsync(
        string threadId,
        string model,
        CancellationToken cancellationToken = default);
}
