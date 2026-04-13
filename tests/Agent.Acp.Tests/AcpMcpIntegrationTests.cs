using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

/// <summary>
/// MCP (Model Context Protocol) integration tests.
/// 
/// Invariant: MCP tools integrate into the same reducer/effect architecture as built-in tools.
/// MCP discovery happens during session setup; discovered tools appear in the catalog and follow
/// the same permission/execution flow.
/// </summary>
public class AcpMcpIntegrationTests
{
    /// <summary>
    /// TC-MCP-001: session/new with mcpServers Triggers tools/list Discovery
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Agents must discover MCP tools during session setup, not lazily. This ensures the tool
    /// catalog is complete before the first prompt, allowing the model to use MCP tools from
    /// the start.
    /// </summary>
    [Fact]
    public async Task SessionNew_WithMcpServers_Triggers_ToolsList_Discovery()
    {
        // ARRANGE: Fake MCP server
        var fakeMcpServer = new FakeMcpServer();
        fakeMcpServer.AddTool(new
        {
            name = "fetch_weather",
            description = "Get current weather",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    location = new { type = "string" },
                },
            },
        });

        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new McpAwareAgentFactory(fakeMcpServer));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        // Initialize
        await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo
                {
                    AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" },
                },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        // Create session with MCP server
        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest
            {
                Cwd = "/tmp",
                McpServers = new List<McpServer>
                {
                    new McpServer
                    {
                        AdditionalProperties = new Dictionary<string, object>
                        {
                            ["stdio"] = new
                            {
                                command = "fake-mcp-server",
                                args = new string[] { },
                            },
                        },
                    },
                },
            },
            cts.Token);

        // ASSERT: Fake MCP server's tools/list was called
        Assert.True(fakeMcpServer.ToolsListCalled,
            "MCP tools/list should be called during session setup");

        cts.Cancel();
        try { await serverTask; } catch { /* ignore */ }
    }

    /// <summary>
    /// TC-MCP-002: Discovered MCP Tools Appear in Tool Catalog
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// MCP tools must be usable in prompts after discovery. The tool catalog rendered for the
    /// session must include MCP-discovered tools alongside built-in tools.
    /// </summary>
    [Fact]
    public async Task DiscoveredMcpTools_AppearIn_ToolCatalog()
    {
        // ARRANGE: Fake MCP server with a tool
        var fakeMcpServer = new FakeMcpServer();
        fakeMcpServer.AddTool(new
        {
            name = "fetch_weather",
            description = "Get current weather for a location",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    location = new { type = "string", description = "City name" },
                },
                required = new[] { "location" },
            },
        });

        // RED: Not implemented yet
        // Implementation driver will:
        // 1. Connect to MCP server during session/new
        // 2. Call tools/list
        // 3. Add discovered tools to session tool catalog
        // 4. Include in agent capabilities / prompt context

        // For now, this test just asserts the invariant
        await Task.CompletedTask;
        
        Assert.Fail("TC-MCP-002 not implemented: Tool catalog should include MCP-discovered 'fetch_weather' tool");
    }

    /// <summary>
    /// TC-MCP-003: Unsupported MCP Transport Rejected During session/new
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Agents must validate MCP transport capabilities and fail fast. Attempting to use an
    /// unsupported transport (e.g., HTTP when only stdio is implemented) should return a clear
    /// error, not silently fail or crash.
    /// </summary>
    [Fact]
    public async Task UnsupportedMcpTransport_Rejected_During_SessionNew()
    {
        // ARRANGE
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new McpAwareAgentFactory(null));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo
                {
                    AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" },
                },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        // ACT: Attempt to create session with unsupported HTTP transport
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
                "session/new",
                new NewSessionRequest
                {
                    Cwd = "/tmp",
                    McpServers = new List<McpServer>
                    {
                        new McpServer
                        {
                            AdditionalProperties = new Dictionary<string, object>
                            {
                                ["http"] = new
                                {
                                    url = "https://mcp.example.com",
                                    headers = new object[] { },
                                },
                            },
                        },
                    },
                },
                cts.Token);
        });

        // ASSERT: Error message indicates unsupported transport
        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);

        cts.Cancel();
        try { await serverTask; } catch { /* ignore */ }
    }

    /// <summary>
    /// TC-MCP-004: MCP Tool Execution Follows Same Permission/Effect Flow
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// MCP tools must integrate into the same reducer/effect architecture as built-in tools.
    /// This ensures consistent behavior, auditability, and permission handling regardless of
    /// tool source.
    /// </summary>
    [Fact]
    public async Task McpToolExecution_Follows_Same_PermissionEffect_Flow()
    {
        // ARRANGE: Fake MCP server with executable tool
        var fakeMcpServer = new FakeMcpServer();
        fakeMcpServer.AddTool(new
        {
            name = "fetch_weather",
            description = "Get weather",
            inputSchema = new { type = "object" },
        });

        fakeMcpServer.OnToolCall = (toolName, args) =>
        {
            if (toolName == "fetch_weather")
            {
                return Task.FromResult<object>(new
                {
                    temperature = 72,
                    condition = "sunny",
                });
            }
            throw new NotSupportedException($"Unknown tool: {toolName}");
        };

        // RED: Not implemented yet
        // Implementation driver will:
        // 1. Model requests fetch_weather
        // 2. Reducer emits CheckPermission effect
        // 3. SessionRunner requests permission via ACP
        // 4. If approved, reducer emits ExecuteToolCall effect
        // 5. SessionRunner routes to MCP executor
        // 6. MCP tools/call is invoked
        // 7. Results feed back through ObservedToolCallCompleted

        await Task.CompletedTask;

        Assert.Fail("TC-MCP-004 not implemented: MCP tool execution should follow same permission/effect flow");
    }

    /// <summary>
    /// TC-MCP-005: MCP Server Connection Failure Emits Observable Error
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Connection failures must not crash the session; they must be observable and recoverable.
    /// The session continues in graceful degradation mode (without that MCP server's tools).
    /// </summary>
    [Fact]
    public async Task McpConnectionFailure_Emits_ObservableError_And_Continues_Session()
    {
        // ARRANGE
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new McpAwareAgentFactory(null));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo
                {
                    AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" },
                },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        // ACT: Create session with nonexistent MCP server binary
        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest
            {
                Cwd = "/tmp",
                McpServers = new List<McpServer>
                {
                    new McpServer
                    {
                        AdditionalProperties = new Dictionary<string, object>
                        {
                            ["stdio"] = new
                            {
                                command = "nonexistent-mcp-binary",
                                args = new string[] { },
                            },
                        },
                    },
                },
            },
            cts.Token);

        // ASSERT: Session was created (graceful degradation)
        Assert.NotNull(newSes.SessionId);

        // RED: Implementation should emit ObservedMcpConnectionFailed
        // and continue session without that MCP server's tools

        cts.Cancel();
        try { await serverTask; } catch { /* ignore */ }
    }
}

/// <summary>
/// Fake in-memory MCP server for testing (no real stdio process).
/// </summary>
internal class FakeMcpServer
{
    private readonly List<object> _tools = new();

    public bool ToolsListCalled { get; private set; }

    public Func<string, object, Task<object>>? OnToolCall { get; set; }

    public void AddTool(object toolDefinition)
    {
        _tools.Add(toolDefinition);
    }

    public Task<object> HandleToolsListAsync()
    {
        ToolsListCalled = true;
        return Task.FromResult<object>(new
        {
            tools = _tools.ToArray(),
        });
    }

    public async Task<object> HandleToolCallAsync(string toolName, object args)
    {
        if (OnToolCall is not null)
        {
            return await OnToolCall(toolName, args);
        }

        throw new NotImplementedException($"Tool call not implemented: {toolName}");
    }
}

/// <summary>
/// Agent factory that integrates fake MCP server.
/// </summary>
internal class McpAwareAgentFactory : IAcpAgentFactory
{
    private readonly FakeMcpServer? _mcpServer;

    public McpAwareAgentFactory(FakeMcpServer? mcpServer)
    {
        _mcpServer = mcpServer;
    }

    public Task<InitializeResponse> InitializeAsync(
        InitializeRequest request,
        CancellationToken cancellationToken)
    {
        // RED: Not implemented yet
        throw new NotImplementedException(
            "McpAwareAgentFactory.InitializeAsync not implemented");
    }

    public async Task<NewSessionResponse> NewSessionAsync(
        NewSessionRequest request,
        CancellationToken cancellationToken)
    {
        // RED: Not implemented yet
        // Implementation driver will:
        // 1. Validate MCP server configs
        // 2. Spawn/connect to MCP servers
        // 3. Call tools/list on each server
        // 4. Aggregate tools into session catalog

        if (_mcpServer is not null && request.McpServers.Any())
        {
            // Simulate tools/list discovery
            await _mcpServer.HandleToolsListAsync();
        }

        throw new NotImplementedException(
            "McpAwareAgentFactory.NewSessionAsync not implemented");
    }

    public IAcpSessionAgent CreateSessionAgent(
        string sessionId,
        IAcpClientCaller client,
        IAcpSessionEvents events)
    {
        // RED: Not implemented yet
        throw new NotImplementedException(
            "McpAwareAgentFactory.CreateSessionAgent not implemented");
    }
}
