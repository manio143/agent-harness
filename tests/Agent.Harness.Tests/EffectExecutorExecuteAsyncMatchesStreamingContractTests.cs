using System.Collections.Immutable;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class EffectExecutorExecuteAsyncMatchesStreamingContractTests
{
    [Fact]
    public async Task ExecuteAsync_matches_ExecuteStreamingAsync_for_all_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-exec-contract", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(root);
        var sessionId = "sess_exec_contract";

        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

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

        var state = SessionState.Empty;

        var effects = ImmutableArray.Create<Effect>(
            new ScheduleWake(ThreadIds.Main),
            new CheckPermission("call_0", "read_text_file", new { path = "/tmp/x" }));

        foreach (var eff in effects)
        {
            var a = await exec.ExecuteAsync(state, eff, CancellationToken.None);

            var list = new List<ObservedChatEvent>();
            await foreach (var o in exec.ExecuteStreamingAsync(state, eff, CancellationToken.None))
                list.Add(o);
            var b = list.ToImmutableArray();

            a.Should().Equal(b);
        }
    }

    private sealed class FakeCaller : Agent.Acp.Acp.IAcpClientCaller
    {
        public Agent.Acp.Schema.ClientCapabilities ClientCapabilities { get; } = new() { Fs = new Agent.Acp.Schema.FileSystemCapabilities() };

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
