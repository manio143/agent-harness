using Agent.Harness.Threads;

namespace Agent.Harness.Tests;

public sealed class TestThreadIdAllocator(string suffix = "0000") : IThreadIdAllocator
{
    public string AllocateThreadId(string name) => $"{name}-{suffix}";
}
