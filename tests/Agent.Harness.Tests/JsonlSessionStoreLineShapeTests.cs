using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class JsonlSessionStoreLineShapeTests
{
    [Fact]
    public void AppendCommitted_Writes_ExactLineShape_ForCommonEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-lineshape-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("sess1", new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        store.AppendCommitted("sess1", new TurnStarted());
        store.AppendCommitted("sess1", new UserMessage("hi"));
        store.AppendCommitted("sess1", new AssistantTextDelta("he"));
        store.AppendCommitted("sess1", new AssistantMessage("hello"));
        store.AppendCommitted("sess1", new TurnEnded());

        var lines = File.ReadAllLines(Path.Combine(dir, "sess1", "events.jsonl"));
        lines.Should().HaveCount(5);

        lines[0].Should().Be(JsonSerializer.Serialize(new { type = "turn_started" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        lines[1].Should().Be(JsonSerializer.Serialize(new { type = "user_message", text = "hi" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        lines[2].Should().Be(JsonSerializer.Serialize(new { type = "assistant_text_delta", textDelta = "he" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        lines[3].Should().Be(JsonSerializer.Serialize(new { type = "assistant_message", text = "hello" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        lines[4].Should().Be(JsonSerializer.Serialize(new { type = "turn_ended" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void AppendCommitted_Writes_ExactLineShape_ForToolCallRejected_WithDetails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-lineshape-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("sess1", new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        var evt = new ToolCallRejected("call_1", "invalid_args", ImmutableArray.Create("missing_required:path"));
        store.AppendCommitted("sess1", evt);

        var line = File.ReadAllLines(Path.Combine(dir, "sess1", "events.jsonl")).Single();

        // Parse and assert to avoid brittle array ordering / whitespace issues beyond the JSON serializer.
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("type").GetString().Should().Be("tool_call_rejected");
        doc.RootElement.GetProperty("toolId").GetString().Should().Be("call_1");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("invalid_args");

        var details = doc.RootElement.GetProperty("details");
        details.ValueKind.Should().Be(JsonValueKind.Array);
        details.EnumerateArray().Select(x => x.GetString()).Should().ContainSingle().Which.Should().Be("missing_required:path");
    }
}
