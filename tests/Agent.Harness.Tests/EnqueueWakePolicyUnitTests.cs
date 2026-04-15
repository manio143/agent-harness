using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class EnqueueWakePolicyUnitTests
{
    [Fact]
    public void ObservedWakeModel_Emits_CallModel_Effect()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedWakeModel());

        result.Effects.Should().ContainSingle().Which.Should().BeOfType<CallModel>();
    }

    [Fact]
    public void HasPendingEnqueue_ReturnsTrue_WhenInboxContainsEnqueue()
    {
        var store = new InMemoryThreadStore();
        var mgr = new ThreadManager("s1", store);

        var child = mgr.New(ThreadIds.Main, "hello", InboxDelivery.Enqueue);

        mgr.HasPendingEnqueue(child).Should().BeTrue();

        // Drain while idle -> clears
        _ = mgr.DrainInboxForPrompt(child);
        mgr.HasPendingEnqueue(child).Should().BeFalse();
    }
}
