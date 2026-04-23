using System.Collections.Generic;
using System.Linq;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MeaiObservedEventSourceToolIntentOrderingTests
{
    [Fact]
    public async Task FromStreamingResponse_WhenToolIntentsOutOfOrder_EmitsReportIntentFirstAtFlushBoundary()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Updates()
        {
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new FunctionCallContent("c1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a" }),
                    new FunctionCallContent("c0", "report_intent", new Dictionary<string, object?> { ["intent"] = "x" }),
                },
                FinishReason = ChatFinishReason.Stop,
            };

            await Task.CompletedTask;
        }

        var observed = new List<ObservedChatEvent>();
        await foreach (var e in MeaiObservedEventSource.FromStreamingResponse(Updates()))
            observed.Add(e);

        var detected = observed.OfType<ObservedToolCallDetected>().ToList();
        detected.Select(d => d.ToolName).Should().Equal("report_intent", "read_text_file");
    }
}
