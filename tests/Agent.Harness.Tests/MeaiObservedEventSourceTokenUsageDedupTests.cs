using System.Collections.Generic;
using System.Linq;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MeaiObservedEventSourceTokenUsageDedupTests
{
    [Fact]
    public async Task FromStreamingResponse_WhenUsageRepeated_DeduplicatesIdenticalUsageEvents()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Updates()
        {
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new UsageContent
                    {
                        Details = new UsageDetails
                        {
                            InputTokenCount = 10,
                            OutputTokenCount = 2,
                            TotalTokenCount = 12,
                        },
                    },
                },
            };

            // Identical repeat (some providers emit cumulative usage multiple times)
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new UsageContent
                    {
                        Details = new UsageDetails
                        {
                            InputTokenCount = 10,
                            OutputTokenCount = 2,
                            TotalTokenCount = 12,
                        },
                    },
                },
            };

            // Finish boundary
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        var observed = new List<ObservedChatEvent>();
        await foreach (var e in MeaiObservedEventSource.FromStreamingResponse(Updates()))
            observed.Add(e);

        observed.OfType<ObservedTokenUsage>().Should().ContainSingle();
    }

    [Fact]
    public async Task FromStreamingResponse_WhenUsageChanges_DoesNotDeduplicate()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Updates()
        {
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new UsageContent
                    {
                        Details = new UsageDetails
                        {
                            InputTokenCount = 10,
                            OutputTokenCount = 2,
                            TotalTokenCount = 12,
                        },
                    },
                },
            };

            // Different usage
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new UsageContent
                    {
                        Details = new UsageDetails
                        {
                            InputTokenCount = 11,
                            OutputTokenCount = 2,
                            TotalTokenCount = 13,
                        },
                    },
                },
            };

            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        var observed = new List<ObservedChatEvent>();
        await foreach (var e in MeaiObservedEventSource.FromStreamingResponse(Updates()))
            observed.Add(e);

        observed.OfType<ObservedTokenUsage>().Should().HaveCount(2);
    }
}
