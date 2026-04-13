using Agent.Harness;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class JsonlSessionStoreTurnRoundtripTests
{
    [Fact]
    public void Append_and_load_roundtrips_turn_markers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-turn-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);

        store.CreateNew("sess1", new SessionMetadata(
            SessionId: "sess1",
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        store.AppendCommitted("sess1", new TurnStarted());
        store.AppendCommitted("sess1", new TurnEnded());

        var loaded = store.LoadCommitted("sess1");
        loaded.Should().HaveCount(2);
        loaded[0].Should().BeOfType<TurnStarted>();
        loaded[1].Should().BeOfType<TurnEnded>();
    }
}
