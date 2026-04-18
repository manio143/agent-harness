using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests.Llm;

public sealed class MeaiObservedEventSourceIncrementalToolCallTests
{
    [Fact]
    public async Task FromStreamingResponse_WhenSameToolCallIdArrivesWithUpdatedArgs_EmitsOnceWithLatestArgs()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Updates()
        {
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new FunctionCallContent("call_1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a" }),
                }
            };

            // Provider repeats same call id with updated args (cumulative delta / incremental args).
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new FunctionCallContent("call_1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/b" }),
                }
            };

            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
            await Task.CompletedTask;
        }

        var events = new List<ObservedChatEvent>();
        await foreach (var e in MeaiObservedEventSource.FromStreamingResponse(Updates()))
            events.Add(e);

        var tool = events.OfType<ObservedToolCallDetected>().Single(e => e.ToolId == "call_1");
        tool.ToolName.Should().Be("read_text_file");

        var args = tool.Args.Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        args["path"].Should().Be("/tmp/b");
    }
}
