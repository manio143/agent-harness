using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests.Llm;

public sealed class MeaiObservedEventSourceTests
{
    [Fact]
    public async Task FromStreamingResponse_WhenProviderRepeatsSameToolCall_DedupesByToolId()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Updates()
        {
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new FunctionCallContent("call_1", "report_intent", new Dictionary<string, object?> { ["intent"] = "x" }),
                }
            };

            // Provider repeats the same tool call in a later update (common with "cumulative" deltas).
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new FunctionCallContent("call_1", "report_intent", new Dictionary<string, object?> { ["intent"] = "x" }),
                }
            };

            // Finish marker
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
            await Task.CompletedTask;
        }

        var events = new List<ObservedChatEvent>();
        await foreach (var e in MeaiObservedEventSource.FromStreamingResponse(Updates()))
            events.Add(e);

        events.OfType<ObservedToolCallDetected>().Where(e => e.ToolId == "call_1").Should().HaveCount(1);
    }
}
