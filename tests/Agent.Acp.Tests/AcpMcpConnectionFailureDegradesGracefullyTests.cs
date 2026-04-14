using Agent.Acp.Schema;
using Agent.Server;

namespace Agent.Acp.Tests;

public sealed class AcpMcpConnectionFailureDegradesGracefullyTests
{
    [Fact]
    public async Task SessionNew_WhenMcpDiscoveryThrows_DoesNotFailSessionNew_AndSurfacesError()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var discovery = new ThrowingMcpDiscovery();
        var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts, discovery);

        var resp = await factory.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer>
            {
                new() { AdditionalProperties = new Dictionary<string, object> { ["stdio"] = new { command = "nonexistent", args = Array.Empty<string>() } } },
            },
        }, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(resp.SessionId));

        // Should surface the error in the response extension payload (without crashing).
        Assert.True(resp.AdditionalProperties.TryGetValue("mcpErrors", out var errorsObj));
        var json = System.Text.Json.JsonSerializer.Serialize(errorsObj, Agent.Acp.Protocol.AcpJson.Options);
        Assert.Contains("mcp discovery failed", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingMcpDiscovery : IMcpDiscovery
    {
        public Task<(System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition> Tools, Agent.Harness.Acp.IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("mcp discovery failed: boom");
    }

    private sealed class NoopChatClient : Microsoft.Extensions.AI.IChatClient
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
}
