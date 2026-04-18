namespace Agent.Harness.Threads;

public interface IThreadMetadataWriter
{
    void ReportIntent(string threadId, string intent);
}
