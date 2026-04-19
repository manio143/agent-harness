using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorCoverageEdgeTests
{
    [Fact]
    public void HasPendingWork_IsTrueAfterScheduleRun()
    {
        var (o, _) = CreateOrchestrator();

        o.ScheduleRun(ThreadIds.Main);

        o.HasPendingWork.Should().BeTrue();
    }

    [Fact]
    public async Task RunUntilQuiescentAsync_WhenToolsNotInitialized_Throws()
    {
        var (o, _) = CreateOrchestrator();

        o.ScheduleRun(ThreadIds.Main);

        var act = async () => await o.RunUntilQuiescentAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("tool_catalog_not_initialized");
    }

    private static (ThreadOrchestrator Orchestrator, string RootDir) CreateOrchestrator()
    {
        var root = Path.Combine(Path.GetTempPath(), "orchestrator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sessionId = "s1";
        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(sessionId, "/repo", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var chat = new NoopChatClient();

        var o = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new StubAcpClientCaller(new ClientCapabilities()),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(),
            logLlmPrompts: false,
            sessionStore: store,
            threadStore: threadStore,
            threads: threads);

        return (o, root);
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return YieldNone();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldNone()
            {
                await Task.CompletedTask;
                yield break;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StubAcpClientCaller : IAcpClientCaller
    {
        public StubAcpClientCaller(ClientCapabilities caps) => ClientCapabilities = caps;

        public ClientCapabilities ClientCapabilities { get; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Client caller not used in this test.");
    }
}
