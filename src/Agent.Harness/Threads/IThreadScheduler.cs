namespace Agent.Harness.Threads;

public interface IThreadScheduler
{
    void ScheduleRun(string threadId);
}
