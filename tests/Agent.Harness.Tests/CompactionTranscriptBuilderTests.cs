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
    public void Build_WhenWriteTextFileHasHugeContent_OmitsLargeContentFromArgs()
    {
        var huge = new string('x', 10_000);

        var committed = ImmutableArray.Create<SessionEvent>(
            new UserMessage("hi"),
            new ToolCallRequested("call_1", "write_text_file", J(new { path = "/tmp/a.txt", content = huge })),
            new ToolCallCompleted("call_1", J(new { ok = true })),
            new AssistantMessage("ok"));

        var text = CompactionTranscriptBuilder.Build(committed);

        text.Should().Contain("write_text_file");
        text.Should().Contain("/tmp/a.txt");
        text.Should().NotContain(huge);
        // JSON escapes '<' as '\u003C' in raw text.
        text.Should().Contain("omitted length=10000");
    }

    [Fact]
    public void Build_WhenPriorCompactionExists_StartsAfterLastThreadCompacted()
    {
        var committed = ImmutableArray.Create<SessionEvent>(
            new UserMessage("old"),
            new ThreadCompacted("<compaction>old</compaction>"),
            new UserMessage("new"));

        var text = CompactionTranscriptBuilder.Build(committed);

        text.Should().NotContain("old");
        text.Should().Contain("new");
    }
}
