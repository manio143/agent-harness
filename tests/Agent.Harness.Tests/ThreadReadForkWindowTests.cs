using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadReadForkWindowTests
{
    [Fact]
    public void ReadThreadMessages_WhenForked_FiltersEverythingBeforeNewThreadTask()
    {
        var store = new InMemoryThreadStore();
        var sessionId = "s";
        store.CreateMainIfMissing(sessionId);

        var childId = "thr_child";
        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: childId,
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: null));

        store.AppendCommittedEvent(sessionId, childId, new UserMessage("before"));
        store.AppendCommittedEvent(sessionId, childId, new NewThreadTask(ThreadId: childId, ParentThreadId: ThreadIds.Main, IsFork: true, Message: "do work"));
        store.AppendCommittedEvent(sessionId, childId, new AssistantMessage("after"));

        var mgr = new ThreadManager(sessionId, store);

        var msgs = mgr.ReadThreadMessages(childId);
        msgs.Select(m => m.Text).Should().NotContain("before");

        msgs[0].Role.Should().Be("system");
        msgs[0].Text.Should().Contain("<thread_created id=\"thr_child\" parent_id=\"main\" />");
        msgs[0].Text.Should().Contain("<notice>");
        msgs[0].Text.Should().Contain("<task>do work</task>");

        msgs.Select(m => m.Text).Should().Contain("after");
    }

    [Fact]
    public void ReadThreadMessages_WhenNotForked_DoesNotFilterBeforeNewThreadTask()
    {
        var store = new InMemoryThreadStore();
        var sessionId = "s";
        store.CreateMainIfMissing(sessionId);

        var childId = "thr_child";
        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: childId,
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: null));

        store.AppendCommittedEvent(sessionId, childId, new UserMessage("before"));
        store.AppendCommittedEvent(sessionId, childId, new NewThreadTask(ThreadId: childId, ParentThreadId: ThreadIds.Main, IsFork: false, Message: "do work"));
        store.AppendCommittedEvent(sessionId, childId, new AssistantMessage("after"));

        var mgr = new ThreadManager(sessionId, store);

        var msgs = mgr.ReadThreadMessages(childId);
        msgs.Select(m => m.Text).Should().Contain("before");
    }
}
