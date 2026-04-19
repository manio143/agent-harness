using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests
{
    internal sealed class TwoPromptChatClient : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;
        public string? LastSecondPromptText { get; private set; }

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            var combined = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));
            if (combined.Contains("Second", StringComparison.Ordinal))
                LastSecondPromptText = combined;

            yield return new Microsoft.Extensions.AI.ChatResponseUpdate
            {
                Contents = new List<Microsoft.Extensions.AI.AIContent>
                {
                    new Microsoft.Extensions.AI.FunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = _calls == 1 ? "first" : "second" }),
                    new Microsoft.Extensions.AI.TextContent(_calls == 1 ? "ok1" : "ok2"),
                }
            };
            await Task.CompletedTask;
        }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(Array.Empty<Microsoft.Extensions.AI.ChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    internal sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException(method);
    }

    internal sealed class NullSessionEvents : IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class NullPromptTurn : IAcpPromptTurn
    {
        public IAcpToolCalls ToolCalls { get; } = new NullToolCalls();

        private sealed class NullToolCalls : IAcpToolCalls
        {
            public IReadOnlyCollection<string> ActiveToolCallIds { get; } = Array.Empty<string>();
            public IAcpToolCall Start(string toolCallId, string title, ToolKind kind) => new NullToolCall(toolCallId);
            public Task CancelAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            private sealed class NullToolCall : IAcpToolCall
            {
                public string ToolCallId { get; }
                public NullToolCall(string toolCallId) => ToolCallId = toolCallId;
                public Task AddContentAsync(ToolCallContent content, CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task InProgressAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task CompletedAsync(CancellationToken cancellationToken = default, object? rawOutput = null) => Task.CompletedTask;
                public Task FailedAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task CancelledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task TwoPrompts_SameSession_DoesNotThrowToolCatalogAlreadyInitialized_AndStillRendersSecondPrompt()
    {
        var sessionId = "ses_two_prompts";
        var root = Path.Combine(Path.GetTempPath(), "harness-two-prompts", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new TwoPromptChatClient();
        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: new NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: Agent.Harness.SessionState.Empty);

        await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Hi" } },
            },
            new NullPromptTurn(),
            CancellationToken.None);

        await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Second" } },
            },
            new NullPromptTurn(),
            CancellationToken.None);

        // If we got here without throwing, we validated the invariant.
        chat.LastSecondPromptText.Should().NotBeNull();
        chat.LastSecondPromptText.Should().Contain("Second");
    }
}
