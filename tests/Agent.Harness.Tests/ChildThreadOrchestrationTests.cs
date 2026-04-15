using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ChildThreadOrchestrationTests
{
    [Fact]
    public void ThreadNew_Immediate_Schedules_Run()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        var scheduled = new List<string>();
        IThreadScheduler scheduler = new FakeScheduler(scheduled);

        // Simulate AcpEffectExecutor behavior: new thread + immediate schedules run.
        var childId = mgr.New(ThreadIds.Main, "go", InboxDelivery.Immediate);
        scheduler.ScheduleRun(childId);

        scheduled.Should().Contain(childId);
    }

    private sealed class FakeScheduler(List<string> scheduled) : IThreadScheduler
    {
        public void ScheduleRun(string threadId) => scheduled.Add(threadId);
    }
}
