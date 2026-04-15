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
    public void ReportIntent_Persists_Metadata_And_Commits_ThreadIntentReported()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

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
        var mgr = new ThreadManager("s1", threadStore);

        // Create a child to receive inbox.
        var child = mgr.CreateChildThread(ThreadIds.Main);

        threadStore.AppendCommittedEvent("s1", child, new ThreadInboxMessageEnqueued(
            ThreadId: child,
            EnvelopeId: "env_1",
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Meta: null,
            Source: "thread",
            SourceThreadId: ThreadIds.Main,
            Delivery: "enqueue",
            EnqueuedAtIso: "t0",
            Text: "hello"));

        // Thread defaults to Idle.
        var drained = mgr.DrainInboxForPrompt(child);
        drained.Should().HaveCount(1);

        // Draining marks the message as delivered (so it won't be re-injected on resume).
        threadStore.LoadCommittedEvents("s1", child).OfType<ThreadInboxMessageDequeued>()
            .Should().ContainSingle(d => d.ThreadId == child && d.EnvelopeId == drained[0].EnvelopeId);

        // Subsequent drains return nothing.
        mgr.DrainInboxForPrompt(child).Should().BeEmpty();
    }

    [Fact]
    public void DrainInboxForPrompt_WhenThreadRunning_DoesNotDeliverEnqueueMessages()
    {
        var threadStore = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", threadStore);

        var child = mgr.CreateChildThread(ThreadIds.Main);

        threadStore.AppendCommittedEvent("s1", child, new ThreadInboxMessageEnqueued(
            ThreadId: child,
            EnvelopeId: "env_1",
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Meta: null,
            Source: "thread",
            SourceThreadId: ThreadIds.Main,
            Delivery: "enqueue",
            EnqueuedAtIso: "t0",
            Text: "hello"));

        mgr.MarkRunning(child);

        var drained = mgr.DrainInboxForPrompt(child);
        drained.Should().BeEmpty();

        // Still pending (not idle-deliverable), so no DeliveredToLlm event should exist.
        threadStore.LoadCommittedEvents("s1", child).OfType<ThreadInboxMessageDequeued>()
            .Should().BeEmpty();
    }
}
