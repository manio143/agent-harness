using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpEffectExecutorThreadToolLegacyPathTests
{
    [Fact]
    public async Task ThreadSend_WhenSchedulerIsMissing_Throws_InsteadOfUsingLegacyEnqueuePath()
    {
        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(
                ToolSchemas.ReportIntent,
                ToolSchemas.ThreadSend,
                ToolSchemas.ThreadList,
                ToolSchemas.ThreadNew,
                ToolSchemas.ThreadFork,
                ToolSchemas.ThreadRead)
        };

        var root = Path.Combine(Path.GetTempPath(), "harness-threadtool-legacy-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata(
            SessionId: "s1",
            Cwd: "/cwd",
            Title: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threads = new ThreadManager("s1", new InMemoryThreadStore());

        // Intentionally omit scheduler/orchestrator to verify we don't fall back to ThreadManager.Send.
        var exec = new AcpEffectExecutor("s1", new FakeCaller(), new NoopChatClient(), store: store, threads: threads, scheduler: null);

        var call = new ExecuteToolCall(
            ToolId: "call_0",
            ToolName: "thread_send",
            Args: new Dictionary<string, object?>
            {
                ["threadId"] = "thr_x",
                ["message"] = "hi",
                ["delivery"] = "immediate",
            });

        var observed = await exec.ExecuteAsync(state, call, CancellationToken.None);

        observed.Any(e => e is ObservedToolCallFailed f && f.Error == "thread_tools_require_orchestrator")
            .Should().BeTrue();
    }

    private sealed class FakeCaller : IAcpClientCaller
    {
        public Agent.Acp.Schema.ClientCapabilities ClientCapabilities { get; } = new();

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class NoopChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> Empty()
            {
                yield break;
            }

            return Empty();
        }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<string> CompleteAsync(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }
}
