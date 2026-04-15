using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Server;

namespace Agent.Acp.Tests;

public sealed class AcpMcpRehydrateCacheTests
{
    [Fact]
    public async Task AfterRehydrate_ResultIsCached_SoSubsequentAgentsDoNotRediscover()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
            Logging = new AgentServerOptions.LoggingOptions { LogLlmPrompts = true },
        };

        // First factory creates session and persists MCP config.
        var factory1 = new AcpHarnessAgentFactory(new EmptyStreamingChatClient(), opts, new NoopMcpDiscovery());

        var ses = await factory1.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer>
            {
                new() { AdditionalProperties = new Dictionary<string, object> { ["stdio"] = new { command = "fake", args = Array.Empty<string>() } } },
            },
        }, CancellationToken.None);

        // New factory simulates a new process: no _mcp cache.
        var discovery = new CountingMcpDiscovery();
        var factory2 = new AcpHarnessAgentFactory(new EmptyStreamingChatClient(), opts, discovery);

        await factory2.LoadSessionAsync(new LoadSessionRequest { SessionId = ses.SessionId, Cwd = cwd, McpServers = new List<McpServer>() }, CancellationToken.None);

        // First agent creation should trigger rehydrate (discovery called once).
        var agent1 = factory2.CreateSessionAgent(ses.SessionId, new NullClientCaller(), new NullSessionEvents());
        await agent1.PromptAsync(new PromptRequest { SessionId = ses.SessionId, Prompt = new List<ContentBlock> { new TextContent { Text = "hi" } } }, new NullPromptTurn(), CancellationToken.None);
        Assert.Single(discovery.Calls);

        // Second agent creation should NOT rediscover.
        var agent2 = factory2.CreateSessionAgent(ses.SessionId, new NullClientCaller(), new NullSessionEvents());
        await agent2.PromptAsync(new PromptRequest { SessionId = ses.SessionId, Prompt = new List<ContentBlock> { new TextContent { Text = "hi" } } }, new NullPromptTurn(), CancellationToken.None);

        Assert.Single(discovery.Calls);

        var promptPath = Path.Combine(cwd, ".agent/sessions", ses.SessionId, "llm.prompt.jsonl");
        Assert.True(File.Exists(promptPath));

        var last = File.ReadLines(promptPath).Last();
        Assert.Contains("fake_mcp_server__echo", last);
    }

    private sealed class NoopMcpDiscovery : IMcpDiscovery
    {
        public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult((ImmutableArray<ToolDefinition>.Empty, (IMcpToolInvoker)NullMcpToolInvoker.Instance));
    }

    private sealed class CountingMcpDiscovery : IMcpDiscovery
    {
        public List<NewSessionRequest> Calls { get; } = new();

        public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            var tool = new ToolDefinition(
                "fake_mcp_server__echo",
                "",
                JsonSerializer.SerializeToElement(new { type = "object" }));
            return Task.FromResult((ImmutableArray.Create(tool), (IMcpToolInvoker)NullMcpToolInvoker.Instance));
        }
    }

    private sealed class EmptyStreamingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NullClientCaller : Agent.Acp.Acp.IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new();
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException(method);
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
