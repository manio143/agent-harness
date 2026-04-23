using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadCompactionCountProjectionTests
{
    private sealed class FakeSessionStore : Agent.Harness.Persistence.ISessionStore
    {
        public void CreateNew(string sessionId, Agent.Harness.Persistence.SessionMetadata metadata) { }

        public bool Exists(string sessionId) => true;

        public ImmutableArray<string> ListSessionIds() => ImmutableArray<string>.Empty;

        public Agent.Harness.Persistence.SessionMetadata? TryLoadMetadata(string sessionId) => null;

        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) => ImmutableArray<SessionEvent>.Empty;

        public void AppendCommitted(string sessionId, SessionEvent evt) { }

        public void UpdateMetadata(string sessionId, Agent.Harness.Persistence.SessionMetadata metadata) { }
    }

    [Fact]
    public async Task MainThreadEventSink_WhenThreadCompacted_IncrementsCompactionCount()
    {
        var store = new InMemoryThreadStore();
        var sessionStore = new FakeSessionStore();

        store.CreateMainIfMissing("s1");

        var sink = new MainThreadEventSink(
            sessionId: "s1",
            threadStore: store,
            appender: store,
            sessionStore: sessionStore,
            logObserved: false);

        var before = store.TryLoadThreadMetadata("s1", ThreadIds.Main)!;
        before.CompactionCount.Should().Be(0);

        await sink.OnCommittedAsync(new ThreadCompacted("<compaction>x</compaction>"));

        var after = store.TryLoadThreadMetadata("s1", ThreadIds.Main)!;
        after.CompactionCount.Should().Be(1);
    }

    [Fact]
    public async Task ThreadEventSink_WhenThreadCompacted_IncrementsCompactionCount()
    {
        var store = new InMemoryThreadStore();
        store.CreateThread("s1", new ThreadMetadata(
            ThreadId: "t1",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "now",
            UpdatedAtIso: "now",
            Model: "default",
            CompactionCount: 0));

        var sink = new ThreadEventSink("s1", "t1", store, store);

        await sink.OnCommittedAsync(new ThreadCompacted("<compaction>x</compaction>"), CancellationToken.None);

        var after = store.TryLoadThreadMetadata("s1", "t1")!;
        after.CompactionCount.Should().Be(1);
    }
}
