using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpEffectExecutorThreadNewUsesOrchestratorForkTests
{
    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "test") }));
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
    public async Task ThreadNew_tool_creates_child_thread_via_orchestrator_owned_lifecycle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-thread-new-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);
        var sessionId = "sess_thread_new";

        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threads = new ThreadManager(sessionId, threadStore);
        var orch = new ThreadOrchestrator(
            sessionId,
            client: new NullClientCaller(),
            chat: new NullChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orch.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        // Build effect executor for main thread.
        var exec = new AcpEffectExecutor(
            sessionId,
            client: new NullClientCaller(),
            chat: new NullChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            store: sessionStore,
            threads: threads,
            scheduler: orch,
            threadId: ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray<ToolDefinition>.Empty };

        // Execute "thread_new" tool effect directly.
        var args = JsonSerializer.SerializeToElement(new { message = "hi", delivery = "immediate" });
        var observed = await exec.ExecuteAsync(state, new ExecuteToolCall(ToolId: "t1", ToolName: "thread_new", Args: args), CancellationToken.None);

        observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle();

        // Run the orchestrator loop so any enqueued observed events are committed.
        await orch.RunUntilQuiescentAsync(CancellationToken.None);

        var allThreads = threadStore.ListThreads(sessionId);
        allThreads.Should().Contain(t => t.ThreadId != ThreadIds.Main && t.ParentThreadId == ThreadIds.Main,
            "thread_new should create a new child thread");
    }
}
