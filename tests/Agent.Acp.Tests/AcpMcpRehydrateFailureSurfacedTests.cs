using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Server;

namespace Agent.Acp.Tests;

public sealed class AcpMcpRehydrateFailureSurfacedTests
{
    [Fact]
    public async Task CreateSessionAgent_WhenMcpRehydrateFails_WritesSessionMcpErrorsFile_AndDoesNotThrow()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        // First factory creates a session and persists MCP config (mcpServers.json).
        var factory1 = new AcpHarnessAgentFactory(new EmptyStreamingChatClient(), opts, new NoopMcpDiscovery());
        var ses = await factory1.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer>
            {
                new() { AdditionalProperties = new Dictionary<string, object> { ["stdio"] = new { command = "fake", args = Array.Empty<string>() } } },
            },
        }, CancellationToken.None);

        // New factory simulates a new process. Rehydrate occurs during session/load (async),
        // so CreateSessionAgent remains non-blocking.
        var factory2 = new AcpHarnessAgentFactory(new EmptyStreamingChatClient(), opts, new ThrowingMcpDiscovery());
        Assert.False(string.IsNullOrWhiteSpace(ses.SessionId));
        var sessionId = ses.SessionId!;

        await factory2!.LoadSessionAsync(new LoadSessionRequest { SessionId = sessionId, Cwd = cwd, McpServers = new List<McpServer>() }, CancellationToken.None);

        var ex = Record.Exception(() => factory2.CreateSessionAgent(sessionId, new NullClientCaller(), new NullSessionEvents()));
        Assert.Null(ex);

        var errPath = Path.Combine(cwd, ".agent/sessions", sessionId, "mcp.errors.jsonl");
        Assert.True(File.Exists(errPath));

        var last = File.ReadLines(errPath).LastOrDefault();
        Assert.NotNull(last);
        Assert.Contains("session_load", last!);
        Assert.Contains("boom", last!);
    }

    private sealed class NoopMcpDiscovery : IMcpDiscovery
    {
        public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult((ImmutableArray<ToolDefinition>.Empty, (IMcpToolInvoker)NullMcpToolInvoker.Instance));
    }

    private sealed class ThrowingMcpDiscovery : IMcpDiscovery
    {
        public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
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
}
