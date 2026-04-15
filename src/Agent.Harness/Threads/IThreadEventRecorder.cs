namespace Agent.Harness.Threads;

public interface IThreadEventRecorder
{
    void InboxEnqueued(ThreadEnvelope envelope, string threadId);

    void InboxDeliveredToLlm(ThreadEnvelope envelope, string threadId);
}
