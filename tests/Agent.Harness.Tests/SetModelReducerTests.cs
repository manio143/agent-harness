using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class SetModelReducerTests
{
    [Fact]
    public void Reduce_WhenObservedSetModel_CommitsSetModel_AndPromptRendersSystemNotice()
    {
        var state = SessionState.Empty;

        var reduced = Core.Reduce(state, new ObservedSetModel(ThreadIds.Main, "granite"));

        reduced.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<SetModel>();

        var prompt = Core.RenderPrompt(reduced.Next);
        prompt.Should().ContainSingle(m => m.Role == ChatRole.System && m.Text == "Inference model has been set to: granite.");
    }
}
