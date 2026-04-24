using System.Collections.Immutable;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesPersistenceRoundTripTests
{
    [Fact]
    public void JsonlThreadStore_RoundTripsThreadMetadataCapabilities()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-capabilities-persistence", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(root);

        store.CreateMainIfMissing("s");

        var meta = new ThreadMetadata(
            ThreadId: "child",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1",
            Mode: ThreadMode.Multi,
            Model: "default",
            Capabilities: new ThreadCapabilitiesSpec(
                Allow: ImmutableArray.Create("fs.read", "mcp:everything"),
                Deny: ImmutableArray.Create("fs.write", "threads")));

        store.CreateThread("s", meta);

        var loaded = store.TryLoadThreadMetadata("s", "child");
        loaded.Should().NotBeNull();
        loaded!.Capabilities.Should().NotBeNull();
        loaded.Capabilities!.Allow.Should().BeEquivalentTo(meta.Capabilities!.Allow);
        loaded.Capabilities!.Deny.Should().BeEquivalentTo(meta.Capabilities!.Deny);
    }

    [Fact]
    public void JsonlThreadStore_LoadsOlderMetadataWithoutCapabilities_AsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-capabilities-persistence", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(root);

        store.CreateMainIfMissing("s");

        var meta = new ThreadMetadata(
            ThreadId: "child",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1",
            Mode: ThreadMode.Multi,
            Model: "default",
            Capabilities: null);

        store.CreateThread("s", meta);

        var loaded = store.TryLoadThreadMetadata("s", "child");
        loaded.Should().NotBeNull();
        loaded!.Capabilities.Should().BeNull();
    }
}
