using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class JsonlSessionStoreTests
{
    private static JsonElement J(object value)
        => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void CreateNew_writes_metadata_and_empty_events_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        var meta = new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: "t",
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z");

        store.CreateNew("sess1", meta);

        store.Exists("sess1").Should().BeTrue();
        store.TryLoadMetadata("sess1").Should().Be(meta);
        store.LoadCommitted("sess1").Should().BeEmpty();
    }

    [Fact]
    public void Append_and_load_roundtrips_committed_events()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("sess1", new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        store.AppendCommitted("sess1", new UserMessage("hi"));
        store.AppendCommitted("sess1", new AssistantTextDelta("Hel"));
        store.AppendCommitted("sess1", new AssistantTextDelta("lo"));
        store.AppendCommitted("sess1", new AssistantMessage("Hello"));
        store.AppendCommitted("sess1", new SessionTitleSet("Greeting"));

        store.LoadCommitted("sess1").Should().Equal(
            new UserMessage("hi"),
            new AssistantTextDelta("Hel"),
            new AssistantTextDelta("lo"),
            new AssistantMessage("Hello"),
            new SessionTitleSet("Greeting"));

        store.TryLoadMetadata("sess1")!.Title.Should().Be("Greeting");
    }

    [Fact]
    public void Append_and_load_roundtrips_tool_call_lifecycle_events()
    {
        // WHY THIS IS AN INVARIANT:
        // Tool call lifecycle is part of the committed session log and must persist for replay/debugging.

        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("sess1", new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        var args = J(new { path = "/tmp/a.txt" });
        store.AppendCommitted("sess1", new ToolCallRequested("call_1", "read_text_file", args));
        store.AppendCommitted("sess1", new ToolCallPermissionApproved("call_1", "capability_present"));
        store.AppendCommitted("sess1", new ToolCallPending("call_1"));
        store.AppendCommitted("sess1", new ToolCallInProgress("call_1"));
        store.AppendCommitted("sess1", new ToolCallUpdate("call_1", J(new { text = "running" })));
        store.AppendCommitted("sess1", new ToolCallCompleted("call_1", J(new { ok = true })));

        var loaded = store.LoadCommitted("sess1");
        loaded.OfType<ToolCallRequested>().Single().Args.GetProperty("path").GetString().Should().Be("/tmp/a.txt");
        loaded.OfType<ToolCallPermissionApproved>().Single().Reason.Should().Be("capability_present");
        loaded.OfType<ToolCallUpdate>().Single().Content.GetProperty("text").GetString().Should().Be("running");
        loaded.OfType<ToolCallCompleted>().Single().Result.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Append_and_load_roundtrips_tool_call_rejection_details()
    {
        // WHY THIS IS AN INVARIANT:
        // Rejection details (e.g. invalid_args reasons) must persist so the ACP client can present
        // the same failure explanation on reload.

        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("sess1", new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        store.AppendCommitted("sess1", new ToolCallRejected(
            "call_1",
            "invalid_args",
            ImmutableArray.Create("missing_required:path", "type_mismatch:mode:string")));

        var loaded = store.LoadCommitted("sess1").OfType<ToolCallRejected>().Single();
        loaded.Reason.Should().Be("invalid_args");
        loaded.Details.Should().Equal("missing_required:path", "type_mismatch:mode:string");
    }

    [Fact]
    public void ListSessionIds_returns_directories_sorted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("b", new SessionMetadata("b", "/tmp", null, "x", "x"));
        store.CreateNew("a", new SessionMetadata("a", "/tmp", null, "x", "x"));

        store.ListSessionIds().Should().Equal("a", "b");
    }
}
