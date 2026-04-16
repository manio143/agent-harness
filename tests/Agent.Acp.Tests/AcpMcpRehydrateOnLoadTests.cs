using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Server;

namespace Agent.Acp.Tests;

public sealed class AcpMcpRehydrateOnLoadTests
{
    [Fact]
    public async Task SessionLoad_WhenMcpConfigPersisted_RehydratesTools_AndAdvertisesToModel()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
            Logging = new AgentServerOptions.LoggingOptions { LogLlmPrompts = true },
            Core = new AgentServerOptions.CoreOptions { CommitAssistantTextDeltas = false },
        };

        var discovery1 = new CapturingMcpDiscovery();
        var factory1 = new AcpHarnessAgentFactory(new EmptyStreamingChatClient(), opts, discovery1);

        // Create session with MCP servers; should persist mcpServers.json.
        var newSession = await factory1.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer>
            {
                new() { AdditionalProperties = new Dictionary<string, object> { ["stdio"] = new { command = "fake", args = Array.Empty<string>() } } },
            },
        }, CancellationToken.None);

        Assert.Single(discovery1.Calls);

        // Simulate new process: new factory with empty in-memory MCP cache.
        var discovery2 = new CapturingMcpDiscovery();
        var factory2 = new AcpHarnessAgentFactory(new EmptyStreamingChatClient(), opts, discovery2);

        // session/load gives cwd again (ACP contract)
        await factory2.LoadSessionAsync(new LoadSessionRequest
        {
            SessionId = newSession.SessionId,
            Cwd = cwd,
            McpServers = new List<McpServer>(),
        }, CancellationToken.None);

        // Rehydrate now occurs during session/load (async).
        Assert.Single(discovery2.Calls);

        // Create agent and force a model call (prompt). This should advertise the MCP tool
        // in the MEAI tool declarations without rediscovering.
        var agent = factory2.CreateSessionAgent(newSession.SessionId, new NullClientCaller(), new NullSessionEvents());

        await agent.PromptAsync(
            new PromptRequest { SessionId = newSession.SessionId, Prompt = new List<ContentBlock> { new TextContent { Text = "hi" } } },
            new NullPromptTurn(),
            CancellationToken.None);

        Assert.Single(discovery2.Calls);

        var promptPath = Path.Combine(cwd, ".agent/sessions", newSession.SessionId, "llm.prompt.jsonl");
        Assert.True(File.Exists(promptPath), "prompt log should exist");

        var last = File.ReadLines(promptPath).Last();
        Assert.Contains("fake_mcp_server__echo", last);
    }

    private sealed class CapturingMcpDiscovery : IMcpDiscovery
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

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new();

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException(method);
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
