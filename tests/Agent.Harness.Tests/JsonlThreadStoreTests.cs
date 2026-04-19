using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class JsonlThreadStoreTests
{
    [Fact]
    public void TryLoadThreadMetadata_WhenMissing_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        store.TryLoadThreadMetadata("s1", "thr_x").Should().BeNull();
    }

    [Fact]
    public void CreateMainIfMissing_IsIdempotent_AndCreatesThreadJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        store.CreateMainIfMissing("s1");
        store.CreateMainIfMissing("s1");

        var meta = store.TryLoadThreadMetadata("s1", ThreadIds.Main);
        meta.Should().NotBeNull();
        meta!.ThreadId.Should().Be(ThreadIds.Main);

        var path = Path.Combine(dir, "s1", "threads", ThreadIds.Main, "thread.json");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void CreateThread_WhenAlreadyExists_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        var meta = new ThreadMetadata(
            ThreadId: "thr_a",
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: "2026-01-01T00:00:00Z",
            UpdatedAtIso: "2026-01-01T00:00:00Z",
            Model: null);

        store.CreateThread("s1", meta);

        var act = () => store.CreateThread("s1", meta);
        act.Should().Throw<InvalidOperationException>().WithMessage("thread_already_exists:thr_a");
    }

    [Fact]
    public void SaveThreadMetadata_CreatesDirectory_AndPersistsJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        var meta = new ThreadMetadata(
            ThreadId: "thr_a",
            ParentThreadId: ThreadIds.Main,
            Intent: "do stuff",
            CreatedAtIso: "2026-01-01T00:00:00Z",
            UpdatedAtIso: "2026-01-01T00:00:00Z",
            Model: "alt");

        store.SaveThreadMetadata("s1", meta);

        var loaded = store.TryLoadThreadMetadata("s1", "thr_a");
        loaded.Should().Be(meta);
    }

    [Fact]
    public void ListThreads_SortsByCreatedAtIso_AndSkipsDirsWithoutMeta()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        // Create directories manually to ensure skip behavior.
        Directory.CreateDirectory(Path.Combine(dir, "s1", "threads", "thr_skip"));

        store.SaveThreadMetadata("s1", new ThreadMetadata(
            ThreadId: "thr_b",
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: "2026-01-02T00:00:00Z",
            UpdatedAtIso: "2026-01-02T00:00:00Z",
            Model: null));

        store.SaveThreadMetadata("s1", new ThreadMetadata(
            ThreadId: "thr_a",
            ParentThreadId: null,
            Intent: null,
            CreatedAtIso: "2026-01-01T00:00:00Z",
            UpdatedAtIso: "2026-01-01T00:00:00Z",
            Model: null));

        var list = store.ListThreads("s1");

        list.Select(m => m.ThreadId).Should().Equal(new[] { "thr_a", "thr_b" });
    }

    [Fact]
    public void LoadCommittedEvents_WhenMissing_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        store.LoadCommittedEvents("s1", "thr_a").Should().BeEmpty();
    }

    [Fact]
    public void AppendCommittedEvent_ThenLoadCommittedEvents_RoundTrips_AndPreservesOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        var args = JsonSerializer.SerializeToElement(new { path = "a.txt" });
        var result = JsonSerializer.SerializeToElement(new { ok = true });

        var evts = ImmutableArray.Create<SessionEvent>(
            new TurnStarted(),
            new UserMessage("hi"),
            new NewThreadTask(ThreadId: "thr_a", ParentThreadId: "main", IsFork: true, Message: "do work"),
            new ToolCallRequested("call_0", "read_text_file", args),
            new ToolCallCompleted("call_0", result));

        foreach (var e in evts)
            store.AppendCommittedEvent("s1", "thr_a", e);

        var loaded = store.LoadCommittedEvents("s1", "thr_a");

        loaded.Length.Should().Be(evts.Length);
        loaded[0].Should().BeOfType<TurnStarted>();
        loaded[1].Should().BeOfType<UserMessage>();
        loaded[2].Should().BeOfType<NewThreadTask>();
        loaded[3].Should().BeOfType<ToolCallRequested>();
        loaded[4].Should().BeOfType<ToolCallCompleted>();

        loaded.OfType<UserMessage>().Single().Text.Should().Be("hi");
        loaded.OfType<NewThreadTask>().Single().Should().Be(new NewThreadTask(ThreadId: "thr_a", ParentThreadId: "main", IsFork: true, Message: "do work"));
    }

    [Fact]
    public void LoadCommittedEvents_IgnoresBlankLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        store.AppendCommittedEvent("s1", "thr_a", new TurnStarted());

        // Append a blank line manually.
        var path = Path.Combine(dir, "s1", "threads", "thr_a", "events.jsonl");
        File.AppendAllText(path, "\n\n");

        var loaded = store.LoadCommittedEvents("s1", "thr_a");
        loaded.Should().ContainSingle(e => e is TurnStarted);
    }

    [Fact]
    public void LoadCommittedEvents_WhenCorruptLine_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jt", Guid.NewGuid().ToString("N"));
        var store = new JsonlThreadStore(dir);

        store.AppendCommittedEvent("s1", "thr_a", new TurnStarted());

        var path = Path.Combine(dir, "s1", "threads", "thr_a", "events.jsonl");
        File.AppendAllText(path, "{not json}\n");

        var act = () => store.LoadCommittedEvents("s1", "thr_a");
        act.Should().Throw<Exception>();
    }
}
