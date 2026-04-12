using System.Text.Json;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SessionMetadataJsonFormatTests
{
    [Fact]
    public void Session_json_has_expected_shape_and_property_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        var meta = new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:01Z");

        store.CreateNew("sess1", meta);

        var sessionDir = Path.Combine(dir, "sess1");
        var path = Path.Combine(sessionDir, "session.json");
        File.Exists(path).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        // Required fields with exact JSON names (camelCase)
        root.GetProperty("sessionId").GetString().Should().Be("sess1");
        root.GetProperty("cwd").GetString().Should().Be("/tmp");
        root.TryGetProperty("title", out var titleEl).Should().BeTrue();
        titleEl.ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("createdAtIso").GetString().Should().Be(meta.CreatedAtIso);
        root.GetProperty("updatedAtIso").GetString().Should().Be(meta.UpdatedAtIso);

        // No accidental PascalCase names.
        root.TryGetProperty("SessionId", out _).Should().BeFalse();
        root.TryGetProperty("CreatedAtIso", out _).Should().BeFalse();

        // Only the expected set (format contract)
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "sessionId",
            "cwd",
            "title",
            "createdAtIso",
            "updatedAtIso",
        });
    }
}
