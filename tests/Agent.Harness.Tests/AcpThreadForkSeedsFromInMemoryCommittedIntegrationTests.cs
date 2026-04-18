using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpThreadForkSeedsFromInMemoryCommittedIntegrationTests
{
    [Fact]
    public async Task ThreadFork_seeds_child_from_in_memory_committed_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-fork-seed", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(root);
        var sessionId = "sess_thread_fork_seed";
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var mainSink = new ThreadEventSink(sessionId, ThreadIds.Main, threadStore);
        await mainSink.OnCommittedAsync(new UserMessage("persisted"), CancellationToken.None);

        var orchestrator = new ThreadOrchestrator(
            sessionId,
            client: new FakeCaller(),
            chat: new NoopChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);
        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ThreadFork),
            Committed = ImmutableArray<SessionEvent>.Empty
                .Add(new UserMessage("persisted"))
                .Add(new UserMessage("unpersisted")),
        };

        var exec = new AcpEffectExecutor(
            sessionId,
            client: new FakeCaller(),
            chat: new NoopChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            logLlmPrompts: false,
            sessionCwd: "/tmp",
            store: sessionStore,
            threadTools: threads,
            observer: orchestrator,
            lifecycle: orchestrator,
            scheduler: orchestrator,
            threadId: ThreadIds.Main);

        var call = new ExecuteToolCall(
            ToolId: "call_0",
            ToolName: "thread_fork",
            Args: new Dictionary<string, object?>
            {
                ["message"] = "child hello",
                ["delivery"] = "immediate",
            });

        var observed = await exec.ExecuteAsync(state, call, CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Single();
        var result = (JsonElement)completed.Result;
        var childId = result.GetProperty("threadId").GetString();
        childId.Should().NotBeNull();

        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        var childCommitted = threadStore.LoadCommittedEvents(sessionId, childId!);
        childCommitted.OfType<UserMessage>().Should().ContainSingle(m => m.Text == "persisted");
        childCommitted.OfType<UserMessage>().Should().ContainSingle(m => m.Text == "unpersisted");
    }

    private sealed class FakeCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class NoopChatClient : Microsoft.Extensions.AI.IChatClient
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
}
