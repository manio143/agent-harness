using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadManagerTests
{
    [Fact]
    public void ThreadNew_CreatesChild_And_EnqueuesInitialMessage()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        var childId = mgr.New(ThreadIds.Main, "hello", InboxDelivery.Immediate);

        childId.Should().StartWith("thr_");

        var threads = mgr.List();
        threads.Should().Contain(t => t.ThreadId == ThreadIds.Main);
        threads.Should().Contain(t => t.ThreadId == childId && t.ParentThreadId == ThreadIds.Main);

        var inbox = store.LoadInbox("s1", childId);
        inbox.Should().ContainSingle();
        inbox[0].Text.Should().Be("hello");
        inbox[0].Source.Should().Be("thread");
        inbox[0].SourceThreadId.Should().Be(ThreadIds.Main);
        inbox[0].Delivery.Should().Be(InboxDelivery.Immediate);
    }

    [Fact]
    public void ThreadFork_DebugAsserts_BufferEmpty_And_CopiesCommittedEvents()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        var parent = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(new UserMessage("u"), new AssistantMessage("a")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<Agent.Harness.ToolDefinition>.Empty);

        var childId = mgr.Fork(ThreadIds.Main, parent, "go", InboxDelivery.Enqueue);

        var evts = store.LoadCommittedEvents("s1", childId);
        evts.OfType<AssistantMessage>().Should().ContainSingle(m => m.Text == "a");
    }

    [Fact]
    public void ReportIntent_Persists_Metadata_And_Commits_ThreadIntentReported()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        mgr.ReportIntent(ThreadIds.Main, "do stuff");

        var meta = store.TryLoadThreadMetadata("s1", ThreadIds.Main);
        meta!.Intent.Should().Be("do stuff");

        var evts = store.LoadCommittedEvents("s1", ThreadIds.Main);
        evts.OfType<ThreadIntentReported>().Should().ContainSingle(i => i.Intent == "do stuff");
    }
}
