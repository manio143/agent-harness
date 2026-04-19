using System.Collections.Immutable;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadManagerTests
{
    [Fact]
    public void List_Includes_Main_And_ChildThreads_FromMetadata()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        // Create child metadata directly in store (thread lifecycle owned by orchestrator).
        var childId = "thr_test";
        threadStore.CreateThread("s1", new ThreadMetadata(
            ThreadId: childId,
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Model: null));

        var threads = mgr.List();
        threads.Should().Contain(t => t.ThreadId == ThreadIds.Main);
        threads.Should().Contain(t => t.ThreadId == childId && t.ParentThreadId == ThreadIds.Main);
    }

    // Forking is now owned by ThreadOrchestrator and tested via:
    // - ThreadOrchestratorRequestForkChildThreadIntegrationTests
    // - HarnessEffectExecutorThreadStartUsesOrchestratorForkTests

    [Fact]
    public void ReportIntent_Persists_Metadata()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        mgr.ReportIntent(ThreadIds.Main, "do stuff");

        var meta = threadStore.TryLoadThreadMetadata("s1", ThreadIds.Main);
        meta!.Intent.Should().Be("do stuff");
        
        // Note: ThreadIntentReported is NOT committed directly by ReportIntent.
        // It's emitted by the reducer when report_intent tool completes.
        // See ReportIntentSinkRoutingIntegrationTests for that behavior.
    }

}
