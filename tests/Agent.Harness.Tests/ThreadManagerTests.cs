using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ThreadManagerTests
{
    [Fact]
    public void HasImmediateOrDeliverableEnqueue_WhenImmediateItemPending_ReturnsTrue()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        store.AppendCommittedEvent("s1", "t1", new ThreadInboxMessageEnqueued(
            ThreadId: "t1",
            EnvelopeId: "e1",
            Kind: ThreadInboxMessageKind.UserPrompt,
            Meta: null,
            Source: "cli",
            SourceThreadId: null,
            Delivery: "immediate",
            EnqueuedAtIso: "2026-01-01T00:00:00Z",
            Text: "hi"));

        mgr.HasImmediateOrDeliverableEnqueue("t1").Should().BeTrue();
    }

    [Fact]
    public void HasImmediateOrDeliverableEnqueue_WhenOnlyEnqueueDeliveryAndIdle_ReturnsTrue()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        store.AppendCommittedEvent("s1", "t1", new ThreadInboxMessageEnqueued(
            ThreadId: "t1",
            EnvelopeId: "e1",
            Kind: ThreadInboxMessageKind.UserPrompt,
            Meta: null,
            Source: "cli",
            SourceThreadId: null,
            Delivery: "enqueue",
            EnqueuedAtIso: "2026-01-01T00:00:00Z",
            Text: "hi"));

        mgr.HasImmediateOrDeliverableEnqueue("t1").Should().BeTrue();
        mgr.HasDeliverableEnqueueNow("t1").Should().BeTrue();
    }

    [Fact]
    public void HasDeliverableEnqueueNow_WhenThreadNotIdle_ReturnsFalse()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        store.AppendCommittedEvent("s1", "t1", new TurnStarted()); // thread is Running
        store.AppendCommittedEvent("s1", "t1", new ThreadInboxMessageEnqueued(
            ThreadId: "t1",
            EnvelopeId: "e1",
            Kind: ThreadInboxMessageKind.UserPrompt,
            Meta: null,
            Source: "cli",
            SourceThreadId: null,
            Delivery: "enqueue",
            EnqueuedAtIso: "2026-01-01T00:00:00Z",
            Text: "hi"));

        mgr.HasDeliverableEnqueueNow("t1").Should().BeFalse();
    }

    [Fact]
    public void ReportIntent_WhenThreadUnknown_Throws()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        var act = () => mgr.ReportIntent("missing", "do stuff");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("unknown_thread:missing");
    }

    [Fact]
    public void GetModel_WhenThreadUnknown_Throws()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        var act = () => mgr.GetModel("missing");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("unknown_thread:missing");
    }

    [Fact]
    public void ReadThreadMessages_ProjectsOnlyKnownMessageTypes()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        store.AppendCommittedEvent("s1", "t1", new UserMessage("u"));
        store.AppendCommittedEvent("s1", "t1", new AssistantMessage("a"));
        store.AppendCommittedEvent("s1", "t1", new ThreadInboxMessageDequeued("t1", "e1", "2026-01-01T00:00:00Z"));
        store.AppendCommittedEvent("s1", "t1", new InterThreadMessage("from", "it"));

        mgr.ReadThreadMessages("t1").Should().BeEquivalentTo(new[]
        {
            new ThreadMessage("user", "u"),
            new ThreadMessage("assistant", "a"),
            new ThreadMessage("inter_thread", "it"),
        });
    }

    [Fact]
    public void List_DefaultsBlankModelToDefault()
    {
        var store = new InMemoryThreadStore();

        // main already exists from CreateMainIfMissing; overwrite metadata with explicit blanks to cover projection.
        store.SaveThreadMetadata("s1", new ThreadMetadata(
            ThreadId: ThreadIds.Main,
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: "2026-01-01T00:00:00Z",
            UpdatedAtIso: "2026-01-01T00:00:00Z",
            Mode: ThreadMode.Multi,
            Model: ""));

        var mgr = new ThreadManager("s1", store);

        mgr.List().Single(t => t.ThreadId == ThreadIds.Main).Model.Should().Be("default");
    }
}
