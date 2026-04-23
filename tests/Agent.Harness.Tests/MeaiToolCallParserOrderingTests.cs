using System.Collections.Generic;
using System.Linq;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MeaiToolCallParserOrderingTests
{
    [Fact]
    public void Parse_WhenMultipleToolCallsInOneUpdate_EmitsReportIntentFirst()
    {
        var update = new ChatResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new FunctionCallContent("c1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a" }),
                new FunctionCallContent("c0", "report_intent", new Dictionary<string, object?> { ["intent"] = "x" }),
            }
        };

        var detected = MeaiToolCallParser.Parse(update).OfType<ObservedToolCallDetected>().ToList();

        detected.Select(d => d.ToolName).Should().Equal("report_intent", "read_text_file");
    }
}
