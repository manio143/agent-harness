using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorRequestForkChildThreadIntegrationTests
{
    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "test")
            }));

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

    [Fact]
    public async Task RequestForkChildThreadAsync_creates_child_and_seeds_parent_history()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "harness-fork-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);

        var sessionId = "sess_fork";
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threads = new ThreadManager(sessionId, threadStore);
        var chat = new NullChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threadAppender: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("seed"));
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        // Act
        var childId = "thr_test_child";
        await orchestrator.RequestForkChildThreadAsync(
            parentThreadId: ThreadIds.Main,
            childThreadId: childId,
            seedCommitted: threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main),
            cancellationToken: CancellationToken.None);
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        // Assert
        var allThreads = threadStore.ListThreads(sessionId);
        var child = allThreads.SingleOrDefault(t => t.ParentThreadId == ThreadIds.Main && t.ThreadId != ThreadIds.Main);
        child.Should().NotBeNull("fork request should create a child thread with parent=main");

        var childCommitted = threadStore.LoadCommittedEvents(sessionId, child!.ThreadId);
        childCommitted.OfType<UserMessage>().Any(m => m.Text == "seed").Should().BeTrue(
            "child thread should be seeded with parent's committed history");
    }
}
