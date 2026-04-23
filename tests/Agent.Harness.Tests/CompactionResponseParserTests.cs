using Agent.Harness.Compaction;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionResponseParserTests
{
    [Fact]
    public void Parse_WhenCompactionBlockExists_ExtractsIt()
    {
        var text = """
noise
<compaction>
## Overview
X
</compaction>
more noise
""";

        var parsed = CompactionResponseParser.Parse(text);

        parsed.Should().Be("<compaction>\n## Overview\nX\n</compaction>");
    }

    [Fact]
    public void Parse_WhenJsonWithProseSummaryExists_UsesProseSummary()
    {
        var text = """
{
  "structured": { "a": 1 },
  "proseSummary": "ok"
}
""";

        var parsed = CompactionResponseParser.Parse(text);

        parsed.Should().Be("ok");
    }

    [Fact]
    public void Parse_WhenNotStructured_FallsBackToTrimmedText()
    {
        CompactionResponseParser.Parse("  hi  ").Should().Be("hi");
    }
}
