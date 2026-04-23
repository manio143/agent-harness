using System.Text.Json;
using Agent.Harness.Compaction;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionResponseParserTests
{
    [Fact]
    public void Parse_WhenLeadingTextExists_ExtractsJsonObject()
    {
        var text = """
<think>
reasoning
</think>

{
  "structured": { "a": 1 },
  "proseSummary": "ok"
}
""";

        var (structured, prose) = CompactionResponseParser.Parse(text);

        structured.GetProperty("a").GetInt32().Should().Be(1);
        prose.Should().Be("ok");
    }

    [Fact]
    public void Parse_WhenReasoningContainsExampleBraces_PicksJsonObjectWithStructuredProseSummary()
    {
        var text = """
<think>
Here is an example: structured: { not json }
And another block: { "notOurSchema": true }
</think>

{"structured": {"a": 2}, "proseSummary": "ok2"}
""";

        var (structured, prose) = CompactionResponseParser.Parse(text);

        structured.GetProperty("a").GetInt32().Should().Be(2);
        prose.Should().Be("ok2");
    }

    [Fact]
    public void Parse_WhenNotJson_FallsBackToRawTextSummary()
    {
        var (structured, prose) = CompactionResponseParser.Parse("not json");

        structured.ValueKind.Should().Be(JsonValueKind.Object);
        prose.Should().Be("not json");
    }
}
