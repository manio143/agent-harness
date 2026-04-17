using System.Text.Json;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests.Threads;

public sealed class ThreadReadMessagesTests
{
    [Fact]
    public void ReadThreadMessages_IncludesUserInterThreadAndAssistant_ExcludesNonMessageEvents()
    {
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing("s1");

        // Simulate a thread event log with mixed event types.
        store.AppendCommittedEvent("s1", ThreadIds.Main, new UserMessage("hi"));
        store.AppendCommittedEvent("s1", ThreadIds.Main, new InterThreadMessage("main", "ping"));
        store.AppendCommittedEvent("s1", ThreadIds.Main, new ToolCallRequested("call1", "thread_list", JsonSerializer.SerializeToElement(new { })));
        store.AppendCommittedEvent("s1", ThreadIds.Main, new AssistantMessage("hello"));

        var mgr = new ThreadManager("s1", store);

        var messages = mgr.ReadThreadMessages(ThreadIds.Main);

        messages.Should().Equal(
            new ThreadMessage("user", "hi"),
            new ThreadMessage("inter_thread", "ping"),
            new ThreadMessage("assistant", "hello"));
    }

    [Fact]
    public void ReadThreadMessages_AllowsAssistantToBeSilent()
    {
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing("s1");

        store.AppendCommittedEvent("s1", ThreadIds.Main, new InterThreadMessage("main", "only inbound"));

        var mgr = new ThreadManager("s1", store);

        var messages = mgr.ReadThreadMessages(ThreadIds.Main);

        messages.Should().Equal(new ThreadMessage("inter_thread", "only inbound"));
    }
}
