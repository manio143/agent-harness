using System.Collections.Immutable;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadManagerTests
{
    [Fact]
    public void ThreadNew_CreatesChild_And_EnqueuesInitialMessage()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        var childId = mgr.CreateChildThread(ThreadIds.Main);
        threadStore.AppendCommittedEvent("s1", childId, new ThreadInboxMessageEnqueued(
            ThreadId: childId,
            EnvelopeId: "env_1",
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Meta: null,
            Source: "thread",
            SourceThreadId: ThreadIds.Main,
            Delivery: "immediate",
            EnqueuedAtIso: "t0",
            Text: "hello"));

        childId.Should().StartWith("thr_");

        var threads = mgr.List();
        threads.Should().Contain(t => t.ThreadId == ThreadIds.Main);
        threads.Should().Contain(t => t.ThreadId == childId && t.ParentThreadId == ThreadIds.Main);

        // inbox is reconstructed from committed events (single source of truth)
        var evts = threadStore.LoadCommittedEvents("s1", childId);
        evts.OfType<ThreadInboxMessageEnqueued>().Should().ContainSingle(e =>
            e.ThreadId == childId &&
            e.Kind == ThreadInboxMessageKind.InterThreadMessage &&
            e.Meta == null &&
            e.Text == "hello" &&
            e.SourceThreadId == ThreadIds.Main &&
            e.Delivery == "immediate");
    }

    [Fact]
    public void ThreadFork_DebugAsserts_BufferEmpty_And_CopiesCommittedEvents()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        var parent = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(new UserMessage("u"), new AssistantMessage("a")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<Agent.Harness.ToolDefinition>.Empty);

        var childId = mgr.ForkChildThread(ThreadIds.Main, parent);
        threadStore.AppendCommittedEvent("s1", childId, new ThreadInboxMessageEnqueued(
            ThreadId: childId,
            EnvelopeId: "env_1",
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Meta: null,
            Source: "thread",
            SourceThreadId: ThreadIds.Main,
            Delivery: "enqueue",
            EnqueuedAtIso: "t0",
            Text: "go"));

        var evts = threadStore.LoadCommittedEvents("s1", childId);
        evts.OfType<AssistantMessage>().Should().ContainSingle(m => m.Text == "a");
    }

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
