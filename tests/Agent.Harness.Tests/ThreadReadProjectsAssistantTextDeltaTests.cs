using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadReadProjectsAssistantTextDeltaTests
{
    [Fact]
    public void ReadThreadMessages_CoalescesAssistantTextDeltasIntoSingleAssistantMessage_WhenNoFinalMessage()
    {
        var sessionId = "s1";
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing(sessionId);

        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: "thr_1",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default"));

        store.AppendCommittedEvent(sessionId, "thr_1", new NewThreadTask(ThreadId: "thr_1", ParentThreadId: ThreadIds.Main, IsFork: false, Message: "do work"));
        store.AppendCommittedEvent(sessionId, "thr_1", new AssistantTextDelta("Hello"));
        store.AppendCommittedEvent(sessionId, "thr_1", new AssistantTextDelta(" world"));

        var mgr = new ThreadManager(sessionId, store);
        var msgs = mgr.ReadThreadMessages("thr_1");

        msgs.Should().Contain(m => m.Role == "system" && m.Text.Contains("<task>", StringComparison.Ordinal));
        msgs.Should().ContainSingle(m => m.Role == "assistant" && m.Text == "Hello world");
    }

    [Fact]
    public void ReadThreadMessages_PrefersFinalAssistantMessage_WhenDeltasAlsoPresent()
    {
        var sessionId = "s1";
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing(sessionId);

        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: "thr_1",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default"));

        store.AppendCommittedEvent(sessionId, "thr_1", new AssistantTextDelta("Hello"));
        store.AppendCommittedEvent(sessionId, "thr_1", new AssistantTextDelta(" world"));
        store.AppendCommittedEvent(sessionId, "thr_1", new AssistantMessage("Hello world"));

        var mgr = new ThreadManager(sessionId, store);
        var msgs = mgr.ReadThreadMessages("thr_1");

        msgs.Where(m => m.Role == "assistant").Should().ContainSingle().Which.Text.Should().Be("Hello world");
    }
}
