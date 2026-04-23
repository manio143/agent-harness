using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Compaction;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionTranscriptBuilderTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Build_ExcludesToolResultBodies_ButIncludesArgsAndStatus()
    {
        var committed = ImmutableArray.Create<SessionEvent>(
            new UserMessage("hi"),
            new ToolCallRequested("call_1", "read_text_file", J(new { path = "/tmp/a.txt" })),
            new ToolCallCompleted("call_1", J(new { veryLargeBody = "SECRET_SHOULD_NOT_APPEAR" })),
            new AssistantMessage("ok"));

        var text = CompactionTranscriptBuilder.Build(committed);

        text.Should().Contain("read_text_file");
        text.Should().Contain("/tmp/a.txt");
        text.Should().Contain("completed");
        text.Should().NotContain("SECRET_SHOULD_NOT_APPEAR");
    }

    [Fact]
    public void Build_WhenPriorCompactionExists_StartsAfterLastCompactionCommitted()
    {
        var committed = ImmutableArray.Create<SessionEvent>(
            new UserMessage("old"),
            new CompactionCommitted(JsonSerializer.SerializeToElement(new { s = 1 }), "old summary"),
            new UserMessage("new"));

        var text = CompactionTranscriptBuilder.Build(committed);

        text.Should().NotContain("old");
        text.Should().Contain("new");
    }
}
