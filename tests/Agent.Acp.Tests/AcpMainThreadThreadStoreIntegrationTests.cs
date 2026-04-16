using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Threads;
using Agent.Server;

namespace Agent.Acp.Tests;

public sealed class AcpMainThreadThreadStoreIntegrationTests
{
    [Fact]
    public async Task AfterPrompt_MainThreadCommittedEvents_ArePersistedInThreadStore()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var factory = new AcpHarnessAgentFactory(new OneTokenStreamingChatClient(), opts, new NoopMcpDiscovery());

        var ses = await factory.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer>(),
        }, CancellationToken.None);

        var agent = factory.CreateSessionAgent(ses.SessionId, new NullClientCaller(), new NullSessionEvents());

        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = ses.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "hi" } },
            },
            new NullPromptTurn(),
            CancellationToken.None);

        var threadRoot = Path.Combine(cwd, ".agent/sessions");
        var threadStore = new JsonlThreadStore(threadRoot);

        var committed = threadStore.LoadCommittedEvents(ses.SessionId, ThreadIds.Main);
        Assert.NotEmpty(committed);
        Assert.Contains(committed, e => e is ThreadInboxMessageEnqueued enq && enq.ThreadId == ThreadIds.Main && enq.Text == "hi");
    }

    private sealed class NoopMcpDiscovery : IMcpDiscovery
    {
        public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult((ImmutableArray<ToolDefinition>.Empty, (IMcpToolInvoker)NullMcpToolInvoker.Instance));
    }

    private sealed class OneTokenStreamingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "title")
            }));

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate
            {
                Contents = new List<Microsoft.Extensions.AI.AIContent> { new Microsoft.Extensions.AI.TextContent("ok") }
            };
        }
    }

    private sealed class NullClientCaller : Agent.Acp.Acp.IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new();
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException(method);
    }

    private sealed class NullSessionEvents : Agent.Acp.Acp.IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullPromptTurn : Agent.Acp.Acp.IAcpPromptTurn
    {
        public Agent.Acp.Acp.IAcpToolCalls ToolCalls { get; } = new NullToolCalls();

        private sealed class NullToolCalls : Agent.Acp.Acp.IAcpToolCalls
        {
            public IReadOnlyCollection<string> ActiveToolCallIds { get; } = Array.Empty<string>();
            public Agent.Acp.Acp.IAcpToolCall Start(string toolCallId, string title, ToolKind kind) => new NullToolCall(toolCallId);
            public Task CancelAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            private sealed class NullToolCall : Agent.Acp.Acp.IAcpToolCall
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
