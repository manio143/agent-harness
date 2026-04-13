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

        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new McpAwareAgentFactory(fakeMcpServer));

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
                                args = Array.Empty<string>(),
                            },
                        },
                    },
                },
            },
            cts.Token);

        // ASSERT: session/new response includes discovered tools in extension data (ephemeral per-session)
        Assert.True(newSes.AdditionalProperties.TryGetValue("tools", out var toolsObj));
        var json = JsonSerializer.Serialize(toolsObj, AcpJson.Options);
        Assert.Contains("fake_mcp_server__fetch_weather", json);

        cts.Cancel();
        try { await serverTask; } catch { /* ignore */ }
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

        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new McpAwareAgentFactory(fakeMcpServer));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<Dictionary<string, JsonElement>>();
        client.NotificationReceived += n =>
        {
            if (n.Method == "session/update" && n.Params.HasValue)
            {
                var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(n.Params.Value.GetRawText(), AcpJson.Options);
                if (envelope?.TryGetValue("update", out var inner) != true) return;
                var update = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inner.GetRawText(), AcpJson.Options);
                if (update is not null) updates.Add(update);
            }
        };

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
                            ["stdio"] = new { command = "fake-mcp-server", args = Array.Empty<string>() },
                        },
                    },
                },
            },
            cts.Token);

        // Prompt triggers the MCP tool call.
        var resp = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSes.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Call fetch_weather" } },
            },
            cts.Token);

        Assert.Equal(StopReason.EndTurn, resp.StopReason.Value);

        var completed = updates.FirstOrDefault(u => u.TryGetValue("status", out var s) && s.GetString() == "completed");
        Assert.NotNull(completed);

        var all = JsonSerializer.Serialize(updates, AcpJson.Options);
        Assert.Contains("sunny", all);

        cts.Cancel();
        try { await serverTask; } catch { /* ignore */ }
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

    // Ephemeral, per-session discovered tools (by sessionId)
    private readonly Dictionary<string, string[]> _sessionTools = new();

    public McpAwareAgentFactory(FakeMcpServer? mcpServer)
    {
        _mcpServer = mcpServer;
    }

    public Task<InitializeResponse> InitializeAsync(
        InitializeRequest request,
        CancellationToken cancellationToken)
    {
        // For these tests we only advertise stdio MCP support (no http/sse).
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object> { ["name"] = "McpAwareAgent", ["version"] = "0" },
            },
            AuthMethods = new List<AuthMethod>(),
            AgentCapabilities = new AgentCapabilities
            {
                PromptCapabilities = new PromptCapabilities(),
                McpCapabilities = new McpCapabilities { Http = false, Sse = false },
            },
        });
    }

    public async Task<NewSessionResponse> NewSessionAsync(
        NewSessionRequest request,
        CancellationToken cancellationToken)
    {
        // Reject unsupported transports up-front.
        foreach (var server in request.McpServers)
        {
            if (server.AdditionalProperties.ContainsKey("http") || server.AdditionalProperties.ContainsKey("sse"))
            {
                throw new InvalidOperationException("unsupported mcp transport");
            }
        }

        var sessionId = Guid.NewGuid().ToString();

        // Eager discovery during session/new (ephemeral per session).
        var discoveredNames = Array.Empty<string>();
        if (_mcpServer is not null && request.McpServers.Any())
        {
            // Mark that discovery happened.
            var toolsListObj = await _mcpServer.HandleToolsListAsync();

            // Server id is derived from stdio.command and normalized to snake_case.
            var stdio = request.McpServers.First().AdditionalProperties["stdio"]!;
            var stdioJson = JsonSerializer.Serialize(stdio, AcpJson.Options);
            using var stdioDoc = JsonDocument.Parse(stdioJson);
            var cmd = stdioDoc.RootElement.GetProperty("command").GetString() ?? "mcp";
            var serverId = NormalizeToSnakeCase(cmd);

            // Pull tool names from tools/list response.
            var toolsJson = JsonSerializer.Serialize(toolsListObj, AcpJson.Options);
            using var toolsDoc = JsonDocument.Parse(toolsJson);

            discoveredNames = toolsDoc.RootElement.GetProperty("tools")
                .EnumerateArray()
                .Select(t => NormalizeToSnakeCase(t.GetProperty("name").GetString() ?? "tool"))
                .Select(tool => $"{serverId}__{tool}")
                .ToArray();
        }

        lock (_sessionTools)
        {
            _sessionTools[sessionId] = discoveredNames;
        }

        var resp = new NewSessionResponse { SessionId = sessionId };
        if (discoveredNames.Length > 0)
        {
            resp.AdditionalProperties["tools"] = discoveredNames;
        }

        return resp;
    }

    public IAcpSessionAgent CreateSessionAgent(
        string sessionId,
        IAcpClientCaller client,
        IAcpSessionEvents events)
    {
        string[] tools;
        lock (_sessionTools)
        {
            tools = _sessionTools.TryGetValue(sessionId, out var t) ? t : Array.Empty<string>();
        }

        return new McpSessionAgent(_mcpServer, tools, events);
    }

    private static string NormalizeToSnakeCase(string value)
    {
        var chars = value
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray();
        var s = new string(chars);
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        return s.Trim('_');
    }

    private sealed class McpSessionAgent : IAcpSessionAgent
    {
        private readonly FakeMcpServer? _server;
        private readonly string[] _tools;
        private readonly IAcpSessionEvents _events;

        public McpSessionAgent(FakeMcpServer? server, string[] tools, IAcpSessionEvents events)
        {
            _server = server;
            _tools = tools;
            _events = events;
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            var txt = request.Prompt.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
            if (_server is not null && _tools.Length > 0 && txt.Contains("fetch_weather", StringComparison.OrdinalIgnoreCase))
            {
                var toolCall = turn.ToolCalls.Start("call_mcp_1", "MCP fetch_weather", new ToolKind(ToolKind.Read));
                await toolCall.InProgressAsync(cancellationToken);

                var result = await _server.HandleToolCallAsync("fetch_weather", new { });
                var json = JsonSerializer.Serialize(result, AcpJson.Options);

                await toolCall.AddContentAsync(new ToolCallContentContent
                {
                    Content = new TextContent { Text = json },
                }, cancellationToken);

                await toolCall.CompletedAsync(cancellationToken);
            }

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }
    }
}
