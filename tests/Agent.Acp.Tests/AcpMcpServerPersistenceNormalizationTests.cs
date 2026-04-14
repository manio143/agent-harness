using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Server;

namespace Agent.Acp.Tests;

public sealed class AcpMcpServerPersistenceNormalizationTests
{
    [Fact]
    public async Task SessionNew_PersistsMcpServers_WithStdioWrapperShape()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts, new NoopMcpDiscovery());

        // Simulate an acpx-like legacy config (flat keys instead of { stdio: {...} }).
        var server = new McpServer
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["name"] = "everything",
                ["command"] = "npx",
                ["args"] = new[] { "-y", "@modelcontextprotocol/server-everything" },
                ["env"] = Array.Empty<object>(),
            }
        };

        var resp = await factory.NewSessionAsync(new NewSessionRequest { Cwd = cwd, McpServers = new List<McpServer> { server } }, CancellationToken.None);

        var path = Path.Combine(cwd, ".agent/sessions", resp.SessionId, "mcpServers.json");
        Assert.True(File.Exists(path));

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        var first = doc.RootElement.EnumerateArray().First();
        Assert.True(first.TryGetProperty("stdio", out var stdio));
        Assert.Equal("npx", stdio.GetProperty("command").GetString());
    }

    private sealed class NoopMcpDiscovery : IMcpDiscovery
    {
        public Task<(System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition> Tools, Agent.Harness.Acp.IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult((System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition>.Empty, (Agent.Harness.Acp.IMcpToolInvoker)Agent.Harness.Acp.NullMcpToolInvoker.Instance));
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
