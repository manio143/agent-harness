using System.Collections.Immutable;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadManagerTests
{
    private static ISessionStore NewSessionStore(string sessionId)
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-threadmanager-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(sessionId, "/tmp", Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"), UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));
        return store;
    }
    [Fact]
    public void ThreadNew_CreatesChild_And_EnqueuesInitialMessage()
    {
        var threadStore = new InMemoryThreadStore();
        var sessionStore = NewSessionStore("s1");
        var mgr = new ThreadManager("s1", threadStore, sessionStore);

        var childId = mgr.New(ThreadIds.Main, "hello", InboxDelivery.Immediate);

        childId.Should().StartWith("thr_");

        var threads = mgr.List();
        threads.Should().Contain(t => t.ThreadId == ThreadIds.Main);
        threads.Should().Contain(t => t.ThreadId == childId && t.ParentThreadId == ThreadIds.Main);

        // inbox is reconstructed from committed events (single source of truth)
        var evts = sessionStore.LoadCommitted("s1");
        evts.OfType<ThreadInboxMessageEnqueued>().Should().ContainSingle(e =>
            e.ThreadId == childId && e.Text == "hello" && e.SourceThreadId == ThreadIds.Main && e.Delivery == "immediate");
    }

    [Fact]
    public void ThreadFork_DebugAsserts_BufferEmpty_And_CopiesCommittedEvents()
    {
        var threadStore = new InMemoryThreadStore();
        var sessionStore = NewSessionStore("s1");
        var mgr = new ThreadManager("s1", threadStore, sessionStore);

        var parent = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(new UserMessage("u"), new AssistantMessage("a")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<Agent.Harness.ToolDefinition>.Empty);

        var childId = mgr.Fork(ThreadIds.Main, parent, "go", InboxDelivery.Enqueue);

        var evts = threadStore.LoadCommittedEvents("s1", childId);
        evts.OfType<AssistantMessage>().Should().ContainSingle(m => m.Text == "a");
    }

    [Fact]
    public void ReportIntent_Persists_Metadata_And_Commits_ThreadIntentReported()
    {
        var threadStore = new InMemoryThreadStore();
        var sessionStore = NewSessionStore("s1");
        var mgr = new ThreadManager("s1", threadStore, sessionStore);

        mgr.ReportIntent(ThreadIds.Main, "do stuff");

        var meta = threadStore.TryLoadThreadMetadata("s1", ThreadIds.Main);
        meta!.Intent.Should().Be("do stuff");

        var evts = threadStore.LoadCommittedEvents("s1", ThreadIds.Main);
        evts.OfType<ThreadIntentReported>().Should().ContainSingle(i => i.Intent == "do stuff");
    }

    [Fact]
    public void DrainInboxForPrompt_WhenThreadIdle_DeliversAndMarksDelivered()
    {
        var threadStore = new InMemoryThreadStore();
        var sessionStore = NewSessionStore("s1");
        var mgr = new ThreadManager("s1", threadStore, sessionStore);

        // Create a child to receive inbox.
        var child = mgr.New(ThreadIds.Main, "hello", InboxDelivery.Enqueue);

        // Thread defaults to Idle.
        var drained = mgr.DrainInboxForPrompt(child);
        drained.Should().HaveCount(1);

        // Draining marks the message as delivered (so it won't be re-injected on resume).
        sessionStore.LoadCommitted("s1").OfType<ThreadInboxMessageDequeued>()
            .Should().ContainSingle(d => d.ThreadId == child && d.EnvelopeId == drained[0].EnvelopeId);

        // Subsequent drains return nothing.
        mgr.DrainInboxForPrompt(child).Should().BeEmpty();
    }

    [Fact]
    public void DrainInboxForPrompt_WhenThreadRunning_DoesNotDeliverEnqueueMessages()
    {
        var threadStore = new InMemoryThreadStore();
        var sessionStore = NewSessionStore("s1");
        var mgr = new ThreadManager("s1", threadStore, sessionStore);

        var child = mgr.New(ThreadIds.Main, "hello", InboxDelivery.Enqueue);

        mgr.MarkRunning(child);

        var drained = mgr.DrainInboxForPrompt(child);
        drained.Should().BeEmpty();

        // Still pending (not idle-deliverable), so no DeliveredToLlm event should exist.
        sessionStore.LoadCommitted("s1").OfType<ThreadInboxMessageDequeued>()
            .Should().BeEmpty();
    }
}
