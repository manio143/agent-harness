using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpThreadSendSelfUsesObserverIntegrationTests
{
    [Fact]
    public async Task ThreadSend_to_self_does_not_require_orchestrator_and_is_returned_as_local_observation()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-send-self", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(root);
        var sessionId = "sess_send_self";

        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ThreadSend),
            Committed = ImmutableArray<SessionEvent>.Empty,
        };

        // No observer/lifecycle/scheduler provided.
        var exec = new HarnessEffectExecutor(
            sessionId,
            client: new FakeCaller(),
            chat: new NoopChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            logLlmPrompts: false,
            sessionCwd: "/tmp",
            store: sessionStore,
            threadTools: threads,
            threadId: ThreadIds.Main);

        var call = new ExecuteToolCall(
            ToolId: "call_0",
            ToolName: "thread_send",
            Args: new Dictionary<string, object?>
            {
                ["threadId"] = ThreadIds.Main,
                ["message"] = "hi",
                ["delivery"] = "immediate",
            });

        var observed = await exec.ExecuteAsync(state, call, CancellationToken.None);

        observed.Should().ContainSingle(e => e is ObservedInboxMessageArrived);
        observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle();

        var completed = observed.OfType<ObservedToolCallCompleted>().Single();
        var result = (JsonElement)completed.Result;
        result.GetProperty("ok").GetBoolean().Should().BeTrue();
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
