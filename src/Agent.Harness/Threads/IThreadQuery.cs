using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public interface IThreadQuery
{
    ImmutableArray<ThreadInfo> List();
    ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId);
}
