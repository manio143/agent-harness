using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorCoverageTests
{
    [Fact]
    public async Task CheckPermission_WhenToolNotInCatalog_Denies()
    {
        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient());

        var state = SessionState.Empty with { Tools = ImmutableArray<ToolDefinition>.Empty };

        var obs = await exec.ExecuteAsync(state, new CheckPermission("p1", "nope", new { }), CancellationToken.None);

        obs.OfType<ObservedPermissionDenied>().Single().Reason.Should().Be("unknown_tool");
    }

    [Fact]
    public async Task CheckPermission_WhenToolInCatalog_Approves()
    {
        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient());

        var schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(new ToolDefinition("t", "", schema)),
        };

        var obs = await exec.ExecuteAsync(state, new CheckPermission("p1", "t", new { }), CancellationToken.None);

        obs.OfType<ObservedPermissionApproved>().Single().Reason.Should().Be("tool_in_catalog");
    }

    [Fact]
    public async Task CallModel_WhenMaxOutputTokensConfigured_SetsChatOptionsMaxOutputTokens()
    {
        var chat = new CapturingOptionsChatClient();

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: chat,
            maxOutputTokensByFriendlyName: friendly => friendly == "alt" ? 123 : null);

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(new SetModel("alt")),
            Tools = ImmutableArray<ToolDefinition>.Empty,
        };

        _ = await exec.ExecuteAsync(state, new CallModel("alt"), CancellationToken.None);

        chat.LastOptions.Should().NotBeNull();
        chat.LastOptions!.MaxOutputTokens.Should().Be(123);
    }

    [Fact]
    public async Task CallModel_WhenLogPromptsEnabled_WritesPromptLog_AndUsesModelSelector_AndWritesConsolePrompt_AndHandlesEmptyTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "heec", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata("s1", "/tmp", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var chatDefault = new RecordingChatClient();
        var chatAlt = new RecordingChatClient();

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: chatDefault,
            chatByModel: model => model == "alt" ? chatAlt : chatDefault,
            logLlmPrompts: true,
            store: store,
            modelCatalogSystemPrompt: "Available inference models: alt\nDefault: alt\nQuick-work: alt");

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(new SetModel("alt")),
            Tools = ImmutableArray<ToolDefinition>.Empty,
        };

        // Use CallModel with explicit model to exercise chatByModel selection.
        var obs = await exec.ExecuteAsync(state, new CallModel("alt"), CancellationToken.None);

        obs.Should().ContainSingle(o => o is ObservedAssistantMessageCompleted);
        chatAlt.Calls.Should().Be(1);

        var logPath = Path.Combine(root, "s1", "llm.prompt.jsonl");
        File.Exists(logPath).Should().BeTrue();
        File.ReadAllText(logPath).Should().Contain("Available inference models");
    }

    private sealed class CapturingOptionsChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return YieldStop();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldStop()
            {
                await Task.CompletedTask;
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
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

    private sealed class NullMeaiChatClient : Microsoft.Extensions.AI.IChatClient
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

    private sealed class NullAcpClientCaller(ClientCapabilities caps) : Agent.Acp.Acp.IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unsupported method: {method}");
    }
}
