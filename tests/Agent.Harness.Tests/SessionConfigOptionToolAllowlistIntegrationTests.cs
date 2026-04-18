using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class SessionConfigOptionToolAllowlistIntegrationTests
{
    [Fact]
    public async Task SetConfigOption_ToolAllowlist_FiltersToolDeclarationsPassedToModel()
    {
        var sessionId = "ses_tool_allowlist";
        var root = Path.Combine(Path.GetTempPath(), "harness-tool-allowlist-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);

        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new CapturingChatClient();

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new NullAcpClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: new NullAcpSessionEvents(),
            coreOptions: new CoreOptions(),
            publishOptions: new AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: SessionState.Empty);

        // Act: restrict tools.
        _ = await agent.SetSessionConfigOptionAsync(
            new SetSessionConfigOptionRequest { SessionId = sessionId, ConfigId = "tool_allowlist", Value = "threading_no_fork" },
            CancellationToken.None)!;

        // Act: prompt once.
        await agent.PromptAsync(
            new PromptRequest { SessionId = sessionId, Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Hi" } } },
            new FakeTurn(),
            CancellationToken.None);

        // Assert: model tool declarations exclude thread_fork.
        chat.LastToolNames.Should().NotContain("thread_fork");
        chat.LastToolNames.Should().Contain("thread_new");
        chat.LastToolNames.Should().Contain("thread_read");
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public ImmutableArray<string> LastToolNames { get; private set; } = ImmutableArray<string>.Empty;

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "OK")
            }));

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastToolNames = options?.Tools?.OfType<Microsoft.Extensions.AI.AIFunctionDeclaration>().Select(t => t.Name).ToImmutableArray() ?? ImmutableArray<string>.Empty;
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate
            {
                Contents = new List<Microsoft.Extensions.AI.AIContent> { new Microsoft.Extensions.AI.TextContent("OK") }
            };
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate { FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Stop };
            await Task.CompletedTask;
        }
    }

    private sealed class NullAcpClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => new() { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true }, Terminal = false };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Client RPC not expected in this test");
    }

    private sealed class NullAcpSessionEvents : IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeTurn : IAcpPromptTurn
    {
        public IAcpToolCalls ToolCalls { get; } = new FakeToolCalls();
    }

    private sealed class FakeToolCalls : IAcpToolCalls
    {
        public IAcpToolCall Start(string toolCallId, string title, ToolKind kind) => new FakeToolCall(toolCallId);
        public IReadOnlyCollection<string> ActiveToolCallIds => Array.Empty<string>();
        public Task CancelAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeToolCall(string toolCallId) : IAcpToolCall
    {
        public string ToolCallId => toolCallId;
        public Task AddContentAsync(Agent.Acp.Schema.ToolCallContent content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InProgressAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompletedAsync(CancellationToken cancellationToken = default, object? rawOutput = null) => Task.CompletedTask;
        public Task FailedAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
