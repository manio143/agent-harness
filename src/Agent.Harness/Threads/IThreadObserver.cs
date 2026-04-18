namespace Agent.Harness.Threads;

public interface IThreadObserver
{
    Task ObserveAsync(string threadId, ObservedChatEvent observed, CancellationToken cancellationToken = default);
}
