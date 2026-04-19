using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ThreadInboxArrivalsTests
{
    [Fact]
    public void UserPrompt_CreatesObservedInboxMessageArrived_WithExpectedDefaults()
    {
        var obs = ThreadInboxArrivals.UserPrompt(
            threadId: "t1",
            text: "hello",
            source: "cli");

        obs.ThreadId.Should().Be("t1");
        obs.Kind.Should().Be(ThreadInboxMessageKind.UserPrompt);
        obs.Source.Should().Be("cli");
        obs.Text.Should().Be("hello");
        obs.SourceThreadId.Should().BeNull();
        obs.Delivery.Should().Be(InboxDelivery.Immediate);
        obs.EnvelopeId.Should().NotBeNullOrWhiteSpace();
        DateTimeOffset.Parse(obs.EnqueuedAtIso).Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void InterThreadMessage_UsesThreadSourceByDefault_AndSetsSourceThreadId()
    {
        var obs = ThreadInboxArrivals.InterThreadMessage(
            threadId: "parent",
            text: "done",
            sourceThreadId: "child");

        obs.ThreadId.Should().Be("parent");
        obs.Kind.Should().Be(ThreadInboxMessageKind.InterThreadMessage);
        obs.Source.Should().Be("thread");
        obs.SourceThreadId.Should().Be("child");
        obs.Text.Should().Be("done");
    }

    [Fact]
    public void ThreadIdleNotification_SetsMetaAndEmptyText()
    {
        var obs = ThreadInboxArrivals.ThreadIdleNotification(
            parentThreadId: "main",
            childThreadId: "child_1",
            lastIntent: "summarize",
            delivery: InboxDelivery.Enqueue);

        obs.ThreadId.Should().Be("main");
        obs.Kind.Should().Be(ThreadInboxMessageKind.ThreadIdleNotification);
        obs.Delivery.Should().Be(InboxDelivery.Enqueue);
        obs.Source.Should().Be("thread");
        obs.SourceThreadId.Should().Be("child_1");
        obs.Text.Should().Be("");
        obs.Meta.Should().BeEquivalentTo(ImmutableDictionary<string, string>.Empty
            .Add(ThreadInboxMetaKeys.ChildThreadId, "child_1")
            .Add(ThreadInboxMetaKeys.LastIntent, "summarize"));
    }
}
