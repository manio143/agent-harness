using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class JsonlSessionStoreTests
{
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
    public void ListSessionIds_returns_directories_sorted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("b", new SessionMetadata("b", "/tmp", null, "x", "x"));
        store.CreateNew("a", new SessionMetadata("a", "/tmp", null, "x", "x"));

        store.ListSessionIds().Should().Equal("a", "b");
    }
}
