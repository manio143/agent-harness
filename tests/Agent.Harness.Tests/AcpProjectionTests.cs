using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpProjectionTests
{
    [Fact]
    public void AssistantMessage_WhenDeltasDisabled_ProjectsMessageChunk()
    {
        var core = new CoreOptions { CommitAssistantTextDeltas = false, CommitReasoningTextDeltas = false };
        var publish = new AcpPublishOptions { PublishReasoning = false };

        var emissions = AcpProjection.Project(new AssistantMessage("hello"), core, publish);

        emissions.Should().Equal(new AcpSendAgentMessageChunk("hello"));
    }

    [Fact]
    public void AssistantMessage_WhenDeltasEnabled_ProjectsNothing()
    {
        var core = new CoreOptions { CommitAssistantTextDeltas = true, CommitReasoningTextDeltas = false };
        var publish = new AcpPublishOptions { PublishReasoning = false };

        var emissions = AcpProjection.Project(new AssistantMessage("hello"), core, publish);

        emissions.Should().BeEmpty();
    }

    [Fact]
    public void AssistantTextDelta_AlwaysProjectsMessageChunk()
    {
        var core = new CoreOptions { CommitAssistantTextDeltas = true, CommitReasoningTextDeltas = false };
        var publish = new AcpPublishOptions { PublishReasoning = false };

        var emissions = AcpProjection.Project(new AssistantTextDelta("hi"), core, publish);

        emissions.Should().Equal(new AcpSendAgentMessageChunk("hi"));
    }

    [Fact]
    public void ReasoningTextDelta_WhenPublishReasoningFalse_ProjectsNothing()
    {
        var core = new CoreOptions { CommitAssistantTextDeltas = true, CommitReasoningTextDeltas = true };
        var publish = new AcpPublishOptions { PublishReasoning = false };

        var emissions = AcpProjection.Project(new ReasoningTextDelta("think"), core, publish);

        emissions.Should().BeEmpty();
    }

    [Fact]
    public void ReasoningTextDelta_WhenPublishReasoningTrue_ProjectsThoughtChunk()
    {
        var core = new CoreOptions { CommitAssistantTextDeltas = true, CommitReasoningTextDeltas = true };
        var publish = new AcpPublishOptions { PublishReasoning = true };

        var emissions = AcpProjection.Project(new ReasoningTextDelta("think"), core, publish);

        emissions.Should().Equal(new AcpSendAgentThoughtChunk("think"));
    }

    [Fact]
    public void ToolCallRejected_ProjectsFailedWithDetails()
    {
        var core = new CoreOptions();
        var publish = new AcpPublishOptions();

        var rejected = new ToolCallRejected(
            ToolId: "call_1",
            Reason: "invalid_args",
            Details: ImmutableArray.Create("missing_required:path"));

        var emissions = AcpProjection.Project(rejected, core, publish);

        emissions.Should().Equal(new AcpToolCallFailed("call_1", "invalid_args: missing_required:path"));
    }

    [Fact]
    public void ToolCallUpdate_ProjectsTextFromJsonStringOrRawJson()
    {
        var core = new CoreOptions();
        var publish = new AcpPublishOptions();

        var str = new ToolCallUpdate("call_1", JsonSerializer.SerializeToElement("hi"));
        AcpProjection.Project(str, core, publish).Should().Equal(new AcpToolCallAddText("call_1", "hi"));

        var obj = new ToolCallUpdate("call_1", JsonSerializer.SerializeToElement(new { a = 1 }));
        AcpProjection.Project(obj, core, publish).Should().Equal(new AcpToolCallAddText("call_1", "{\"a\":1}"));
    }
}
