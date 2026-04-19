using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadConfigToolEmitsSetModelReducerTests
{
    [Fact]
    public async Task thread_config_emits_ObservedSetModel_and_reducer_commits_SetModel()
    {
        var sessionId = "s_thread_config";
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-config-tool-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var tools = Core.RenderToolCatalog(new Agent.Acp.Schema.ClientCapabilities());
        var state = new SessionState(
            ImmutableArray<SessionEvent>.Empty,
            TurnBuffer.Empty,
            tools);

        var exec = new HarnessEffectExecutor(
            sessionId,
            client: new NullClientCaller(),
            chat: new NullChatClient(),
            chatByModel: _ => new NullChatClient(),
            isKnownModel: m => string.Equals(m, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(m, "m2", StringComparison.OrdinalIgnoreCase),
            mcp: NullMcpToolInvoker.Instance,
            logLlmPrompts: false,
            store: store,
            threadTools: null,
            observer: null,
            scheduler: null,
            lifecycle: null,
            threadId: ThreadIds.Main,
            sessionCwd: "/tmp");

        // Tool call flow is: request permission -> execute tool.
        var toolId = "tool_1";
        var args = JsonSerializer.SerializeToElement(new { model = "m2" });

        // CheckPermission is deterministic and should approve when tool exists in catalog.
        var observed1 = await exec.ExecuteAsync(state, new CheckPermission(toolId, "thread_config", args), CancellationToken.None);
        observed1.Should().ContainSingle(o => o is ObservedPermissionApproved);

        // Execute tool.
        var observed2 = await exec.ExecuteAsync(state, new ExecuteToolCall(toolId, "thread_config", args), CancellationToken.None);
        observed2.Should().Contain(o => o is ObservedSetModel);

        // Reduce the observed set-model event.
        var setObs = (ObservedSetModel)observed2.First(o => o is ObservedSetModel);
        setObs.Model.Should().Be("m2");

        var reduced = Core.Reduce(state, setObs);
        reduced.NewlyCommitted.Should().ContainSingle(e => e is SetModel);
        ((SetModel)reduced.NewlyCommitted.Single(e => e is SetModel)).Model.Should().Be("m2");
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public Agent.Acp.Schema.ClientCapabilities ClientCapabilities { get; } = new();

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"NullClientCaller should not be used for method: {method}");
    }

    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(Array.Empty<Microsoft.Extensions.AI.ChatMessage>()));

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Empty();

        private static async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
