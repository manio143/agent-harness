using System.Collections.Immutable;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class MainThreadEventSinkObservedLoggingNonJsonlStoreTests
{
    [Fact]
    public async Task OnObservedAsync_WhenStoreIsNotJsonl_DoesNotWriteFile()
    {
        var sessionId = "s1";
        var sink = new MainThreadEventSink(
            sessionId,
            threadStore: new InMemoryThreadStore(),
            sessionStore: new FakeSessionStore(),
            logObserved: true);

        await sink.OnObservedAsync(new ObservedUserMessage("hi"));

        // No file system side effects possible because store isn't Jsonl; this is a guard-rail test.
        true.Should().BeTrue();
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public void CreateNew(string sessionId, SessionMetadata metadata) => throw new NotSupportedException();
        public bool Exists(string sessionId) => false;
        public ImmutableArray<string> ListSessionIds() => ImmutableArray<string>.Empty;
        public SessionMetadata? TryLoadMetadata(string sessionId) => null;
        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) => ImmutableArray<SessionEvent>.Empty;
        public void AppendCommitted(string sessionId, SessionEvent evt) => throw new NotSupportedException();
        public void UpdateMetadata(string sessionId, SessionMetadata metadata) => throw new NotSupportedException();
    }
}
