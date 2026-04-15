using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ReasoningMessageCommitTests
{
    [Fact]
    public void ObservedReasoningMessageCompleted_WhenReasoningBuffered_Commits_ReasoningMessage_And_ClearsBuffer()
    {
        var state = SessionState.Empty with
        {
            Buffer = new TurnBuffer(
                AssistantText: "",
                AssistantMessageOpen: false,
                ReasoningText: "think",
                ReasoningMessageOpen: true,
                IntentReportedThisTurn: false)
        };

        var result = Core.Reduce(state, new ObservedReasoningMessageCompleted());

        result.NewlyCommitted.Should().ContainSingle().Which.Should().BeOfType<ReasoningMessage>()
            .Which.Text.Should().Be("think");

        result.Next.Buffer.ReasoningText.Should().BeEmpty();
        result.Next.Buffer.ReasoningMessageOpen.Should().BeFalse();
    }
}
