using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorForkSeedsFromProvidedSnapshotIntegrationTests
{
    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(Array.Empty<Microsoft.Extensions.AI.ChatMessage>()));

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
    public async Task Fork_uses_provided_seed_snapshot_instead_of_reloading_store()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-fork-seed", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);
        var sessionId = "sess_fork_seed";

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
            threads: threads);

        orch.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        // Persist one committed event to main, to prove we are starting from a store-backed snapshot.
        await orch.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("persisted"));
        await orch.RunUntilQuiescentAsync(CancellationToken.None);

        var fromStore = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        fromStore.OfType<UserMessage>().Should().ContainSingle(m => m.Text == "persisted");

        // Simulate mid-turn state: include an additional committed event that is NOT in the store yet.
        // The fork request must seed from this provided snapshot (not by reloading from threadStore).
        var seed = fromStore.Add(new UserMessage("unpersisted"));

        var childId = "thr_child_seed";
        await orch.RequestForkChildThreadAsync(
            parentThreadId: ThreadIds.Main,
            childThreadId: childId,
            seedCommitted: seed,
            cancellationToken: CancellationToken.None);

        var childCommitted = threadStore.LoadCommittedEvents(sessionId, childId);
        childCommitted.OfType<UserMessage>().Should().ContainSingle(m => m.Text == "persisted");
        childCommitted.OfType<UserMessage>().Should().ContainSingle(m => m.Text == "unpersisted");
    }
}
