namespace Agent.Harness.Threads;

public interface IThreadIdAllocator
{
    string AllocateThreadId(string name);
}
