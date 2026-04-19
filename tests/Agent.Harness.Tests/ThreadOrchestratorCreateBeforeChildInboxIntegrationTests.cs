using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorCreateBeforeChildInboxIntegrationTests
{
    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(Array.Empty<Microsoft.Extensions.AI.ChatMessage>()));
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

    [Fact]
    public async Task CreateRequest_is_processed_before_child_inbox_observation_is_committed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-thread-order", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);
        var sessionId = "sess_order";

        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0"));

        var threads = new ThreadManager(sessionId, threadStore);
        var chat = new NullChatClient();
        var orch = new ThreadOrchestrator(
            sessionId,
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

        orch.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        // Seed some committed history on main.
        await orch.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("seed"));
        await orch.RunUntilQuiescentAsync(CancellationToken.None);

        var childId = "thr_child";

        // Enqueue create request AND a child inbox message, then let the orchestrator run.
        await orch.RequestForkChildThreadAsync(
            parentThreadId: ThreadIds.Main,
            childThreadId: childId,
            seedCommitted: threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main),
            cancellationToken: CancellationToken.None);

        await orch.ObserveAsync(
            childId,
            ThreadInboxArrivals.InterThreadMessage(
                threadId: childId,
                text: "child hello",
                sourceThreadId: ThreadIds.Main,
                source: "thread",
                delivery: InboxDelivery.Immediate),
            CancellationToken.None);

        await orch.RunUntilQuiescentAsync(CancellationToken.None);

        threadStore.TryLoadThreadMetadata(sessionId, childId).Should().NotBeNull();

        var committed = threadStore.LoadCommittedEvents(sessionId, childId);
        committed.OfType<ThreadInboxMessageEnqueued>()
            .Should().Contain(e => e.Kind == ThreadInboxMessageKind.InterThreadMessage && e.Text.Contains("child hello"));
    }
}
