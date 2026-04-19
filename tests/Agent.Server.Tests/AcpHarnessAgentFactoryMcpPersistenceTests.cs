using System.Text.Json;
using Agent.Acp.Schema;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Server.Tests;

public sealed class AcpHarnessAgentFactoryMcpPersistenceTests
{
    [Fact]
    public async Task NewSessionAsync_WhenMcpServersProvided_PersistsNormalizedStdioWrappedConfig()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "ahaf-mcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts, mcpDiscovery: new NoopDiscovery());

        var flattened = new McpServer();
        flattened.AdditionalProperties["name"] = "srv";
        flattened.AdditionalProperties["command"] = "node";
        flattened.AdditionalProperties["args"] = new[] { "-v" };
        flattened.AdditionalProperties["env"] = new Dictionary<string, string> { ["X"] = "1" };

        var resp = await factory.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer> { flattened },
        }, CancellationToken.None);

        var sessionId = resp.SessionId;
        var rootDir = Path.GetFullPath(Path.Combine(cwd, opts.Sessions.Directory));
        var mcpConfigPath = Path.Combine(rootDir, sessionId, "mcpServers.json");

        File.Exists(mcpConfigPath).Should().BeTrue();

        var json = JsonDocument.Parse(File.ReadAllText(mcpConfigPath)).RootElement;
        json.ValueKind.Should().Be(JsonValueKind.Array);

        var server = json[0];
        server.TryGetProperty("stdio", out var stdio).Should().BeTrue();
        stdio.GetProperty("name").GetString().Should().Be("srv");
        stdio.GetProperty("command").GetString().Should().Be("node");
    }

    [Fact]
    public async Task NewSessionAsync_WhenMcpDiscoveryFails_ReturnsMcpErrors_AndAppendsErrorsJsonl()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "ahaf-mcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts, mcpDiscovery: new ThrowingDiscovery());

        var wrapped = new McpServer();
        wrapped.AdditionalProperties["stdio"] = new Dictionary<string, object?> { ["name"] = "srv", ["command"] = "node" };

        var resp = await factory.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer> { wrapped },
        }, CancellationToken.None);

        resp.AdditionalProperties.Should().ContainKey("mcpErrors");

        var rootDir = Path.GetFullPath(Path.Combine(cwd, opts.Sessions.Directory));
        var errorsPath = Path.Combine(rootDir, resp.SessionId, "mcp.errors.jsonl");

        File.Exists(errorsPath).Should().BeTrue();
        File.ReadAllText(errorsPath).Should().Contain("\"phase\":\"session_new\"");
    }

    private sealed class NoopDiscovery : IMcpDiscovery
    {
        public Task<(System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition> Tools, Agent.Harness.Acp.IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult((System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition>.Empty, (Agent.Harness.Acp.IMcpToolInvoker)Agent.Harness.Acp.NullMcpToolInvoker.Instance));
    }

    private sealed class ThrowingDiscovery : IMcpDiscovery
    {
        public Task<(System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition> Tools, Agent.Harness.Acp.IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class NoopChatClient : IChatClient
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
}
