using Agent.Harness;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MeaiObservedEventSourceMessageBoundaryTests
{
    [Fact]
    public async Task FromStreamingResponse_WhenTextThenToolCallInSameUpdate_EmitsMessageCompletedBeforeToolCall()
    {
        var update = new ChatResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new TextContent("hello"),
                new FunctionCallContent("call_1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" }),
            }
        };

        var observed = await Drain(MeaiObservedEventSource.FromStreamingResponse(One(update)));

        observed.Select(e => e.GetType()).Should().ContainInOrder(
            typeof(ObservedAssistantTextDelta),
            typeof(ObservedAssistantMessageCompleted),
            typeof(ObservedToolCallDetected));

        observed.OfType<ObservedAssistantMessageCompleted>().First().FinishReason.Should().BeNull();
    }

    [Fact]
    public async Task FromStreamingResponse_WhenTextThenToolCallAcrossUpdates_EmitsMessageCompletedBeforeToolCall()
    {
        var u1 = new ChatResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new TextContent("hello"),
            }
        };

        var u2 = new ChatResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new FunctionCallContent("call_1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" }),
            }
        };

        var observed = await Drain(MeaiObservedEventSource.FromStreamingResponse(Many(u1, u2)));

        // We should close the assistant message before we emit the tool call.
        var toolIndex = observed.FindIndex(e => e is ObservedToolCallDetected);
        var closeIndex = observed.FindIndex(e => e is ObservedAssistantMessageCompleted);

        closeIndex.Should().BeGreaterThanOrEqualTo(0);
        toolIndex.Should().BeGreaterThan(closeIndex);
    }

    [Fact]
    public async Task FromStreamingResponse_WhenTextThenReasoning_ClosesAssistantMessageBeforeReasoningStarts()
    {
        var update = new ChatResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new TextContent("hello"),
                new TextReasoningContent("thinking"),
            }
        };

        var observed = await Drain(MeaiObservedEventSource.FromStreamingResponse(One(update)));

        observed.Select(e => e.GetType()).Should().ContainInOrder(
            typeof(ObservedAssistantTextDelta),
            typeof(ObservedAssistantMessageCompleted),
            typeof(ObservedReasoningTextDelta));
    }

    [Fact]
    public async Task FromStreamingResponse_WhenReasoningThenToolCall_ClosesReasoningBeforeToolCall()
    {
        var update = new ChatResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new TextReasoningContent("thinking"),
                new FunctionCallContent("call_1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" }),
            }
        };

        var observed = await Drain(MeaiObservedEventSource.FromStreamingResponse(One(update)));

        observed.Select(e => e.GetType()).Should().ContainInOrder(
            typeof(ObservedReasoningTextDelta),
            typeof(ObservedReasoningMessageCompleted),
            typeof(ObservedToolCallDetected));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> One(ChatResponseUpdate u)
    {
        yield return u;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Many(params ChatResponseUpdate[] updates)
    {
        foreach (var u in updates)
            yield return u;
        await Task.CompletedTask;
    }

    private static async Task<List<ObservedChatEvent>> Drain(IAsyncEnumerable<ObservedChatEvent> src)
    {
        var list = new List<ObservedChatEvent>();
        await foreach (var e in src)
            list.Add(e);
        return list;
    }
}
