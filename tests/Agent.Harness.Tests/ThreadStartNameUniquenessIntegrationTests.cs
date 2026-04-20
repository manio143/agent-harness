using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadStartNameUniquenessIntegrationTests
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
    public async Task thread_start_name_must_be_unique_within_session()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "harness-thread-start-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);

        var sessionId = "sess_thread_start_unique";
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

        var exec = new SystemToolCallExecutor(
            threadTools: orchestrator,
            observer: orchestrator,
            lifecycle: orchestrator,
            scheduler: orchestrator,
            isKnownModel: null,
            threadId: ThreadIds.Main);

        var args = new { name = "child", context = "new", message = "do work", delivery = "immediate" };

        // Act
        var first = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "thread_start", args), CancellationToken.None);
        var second = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t2", "thread_start", args), CancellationToken.None);

        // Assert
        first.Should().ContainSingle(e => e is ObservedToolCallCompleted);
        second.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("thread_already_exists:child");

        threadStore.ListThreads(sessionId).Select(t => t.ThreadId).Should().Contain("child");
    }
}
