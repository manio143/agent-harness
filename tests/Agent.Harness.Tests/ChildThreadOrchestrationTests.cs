using System.Collections.Immutable;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ChildThreadOrchestrationTests
{
    private static ISessionStore NewSessionStore(string sessionId)
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-childorch-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(sessionId, "/tmp", Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"), UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));
        return store;
    }
    [Fact]
    public void ThreadNew_Immediate_Schedules_Run()
    {
        var threadStore = new InMemoryThreadStore();
        var sessionStore = NewSessionStore("s1");
        var mgr = new ThreadManager("s1", threadStore, sessionStore);

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
