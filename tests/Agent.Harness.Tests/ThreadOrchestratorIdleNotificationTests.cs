using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorIdleNotificationTests
{
    [Fact]
    public async Task ChildIdleNotification_EnqueuesTypedInboxMessageToParent()
    {
        var sessionId = "s1";

        var sessionRoot = Path.Combine(Path.GetTempPath(), "harness-orchestrator-idle-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(sessionRoot);
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threadStore = new InMemoryThreadStore();

        // ThreadManager will create main thread metadata.
        var threads = new ThreadManager(sessionId, threadStore, sessionStore);

        // Create child thread metadata (no inbox messages => fully idle => notify parent on scheduled run).
        var childId = "thr_child";
        var now = DateTimeOffset.UtcNow.ToString("O");
        threadStore.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: childId,
            ParentThreadId: ThreadIds.Main,
            Intent: "doing work",
            CreatedAtIso: now,
            UpdatedAtIso: now,
            Status: ThreadStatus.Idle));

        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new FakeClientCaller(),
            chat: new ThrowingChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(),
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orchestrator.ScheduleRun(childId);
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        var committed = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        committed.OfType<ThreadInboxMessageEnqueued>().Should().ContainSingle(e =>
            e.ThreadId == ThreadIds.Main &&
            e.Kind == ThreadInboxMessageKind.ThreadIdleNotification &&
            e.Meta != null &&
            e.Meta["childThreadId"] == childId &&
            e.Meta["lastIntent"] == "doing work");
    }

    private sealed class FakeClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Chat should not be called in this test");

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Chat should not be called in this test");
    }
}
