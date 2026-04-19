using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class HarnessAcpSessionAgentLoadsCommittedFromThreadStoreTests
{
    [Fact]
    public async Task PromptAsync_LoadsMainCommittedFromThreadStore_BeforeCallingModel()
    {
        var sessionId = "ses_load_committed";
        var root = Path.Combine(Path.GetTempPath(), "harness-load-committed", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threadStore = new JsonlThreadStore(root);
        threadStore.CreateMainIfMissing(sessionId);

        // Pre-seed committed history directly in the main thread log.
        threadStore.AppendCommittedEvent(sessionId, ThreadIds.Main, new UserMessage("preexisting"));

        var client = new NullClientCaller();
        var events = new NullSessionEvents();
        var chat = new InspectingChatClient();

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: client,
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: events,
            coreOptions: new CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: SessionState.Empty);

        await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Hi" } },
            },
            new NullPromptTurn(),
            CancellationToken.None);

        chat.SawPreexisting.Should().BeTrue();
    }

    private sealed class InspectingChatClient : MeaiIChatClient
    {
        public bool SawPreexisting { get; private set; }

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            SawPreexisting = messages.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.User && (m.Text?.Contains("preexisting", StringComparison.Ordinal) ?? false));
            return Respond();
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Respond()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "test" }),
                    new MeaiTextContent("ok"),
                }
            };
            await Task.CompletedTask;
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException(method);
    }

    private sealed class NullSessionEvents : IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullPromptTurn : IAcpPromptTurn
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
}
