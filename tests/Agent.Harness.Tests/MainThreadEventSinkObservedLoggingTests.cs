using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class MainThreadEventSinkObservedLoggingTests
{
    [Fact]
    public async Task OnObservedAsync_WhenEnabledAndJsonlStore_AppendsObservedJsonl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "main-sink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var sessionId = "s1";
        var store = new JsonlSessionStore(dir);
        store.CreateNew(sessionId, new SessionMetadata(sessionId, "/repo", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var threads = new InMemoryThreadStore();
        var sink = new MainThreadEventSink(sessionId, threads, store, logObserved: true);

        await sink.OnObservedAsync(new ObservedUserMessage("hi"));

        var path = Path.Combine(dir, sessionId, "observed.jsonl");
        File.Exists(path).Should().BeTrue();

        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("obs_user_message");
        lines[0].Should().Contain("hi");
    }

    [Fact]
    public async Task OnObservedAsync_WhenDisabled_DoesNotWriteFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "main-sink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var sessionId = "s1";
        var store = new JsonlSessionStore(dir);
        store.CreateNew(sessionId, new SessionMetadata(sessionId, "/repo", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var threads = new InMemoryThreadStore();
        var sink = new MainThreadEventSink(sessionId, threads, store, logObserved: false);

        await sink.OnObservedAsync(new ObservedUserMessage("hi"));

        var path = Path.Combine(dir, sessionId, "observed.jsonl");
        File.Exists(path).Should().BeFalse();
    }
}
