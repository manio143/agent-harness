using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using ModelContextProtocol.Client;

using McpRequestMethods = ModelContextProtocol.Protocol.RequestMethods;
using McpITransport = ModelContextProtocol.Protocol.ITransport;
using McpJsonRpcMessage = ModelContextProtocol.Protocol.JsonRpcMessage;
using McpJsonRpcRequest = ModelContextProtocol.Protocol.JsonRpcRequest;
using McpJsonRpcResponse = ModelContextProtocol.Protocol.JsonRpcResponse;
using McpTool = ModelContextProtocol.Protocol.Tool;
using McpListToolsResult = ModelContextProtocol.Protocol.ListToolsResult;
using McpCallToolRequestParams = ModelContextProtocol.Protocol.CallToolRequestParams;
using McpCallToolResult = ModelContextProtocol.Protocol.CallToolResult;
using McpContentBlock = ModelContextProtocol.Protocol.ContentBlock;
using McpTextContentBlock = ModelContextProtocol.Protocol.TextContentBlock;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class ModeAMcpToolIntegrationTests
{
    [Fact]
    public async Task ModeA_ModelCallsMcpTool_ToolExecutes_RePrompt_CompletesTurn()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        // No ACP filesystem/terminal calls should happen in this test.
        client.RequestHandler = (req, _) => throw new InvalidOperationException($"Unexpected agent->client request: {req.Method}");

        await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities(), Terminal = false },
            },
            cts.Token);

        var newSession = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var resp = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Call the MCP echo tool" } },
            },
            cts.Token);

        resp.StopReason.Value.Should().Be(StopReason.EndTurn);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            var dir = Path.Combine(Path.GetTempPath(), "harness-mcp-tool-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(dir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions();
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            // MCP client wired to an in-memory MCP server.
            var mcp = InMemoryMcp.CreateEchoServerClientAsync().GetAwaiter().GetResult();
            var tools = mcp.ListToolsAsync().GetAwaiter().GetResult();

            // The tool is exposed to the LLM as {server}__{tool}.
            var serverId = "fake_server";
            var toolName = "echo";
            var exposedName = $"{serverId}__{toolName}";

            var tool = tools.Single(t => t.Name == toolName);

            var invoker = new SdkMcpToolInvoker(new Dictionary<string, McpClientTool>
            {
                [exposedName] = tool,
            });

            var initialState = SessionState.Empty with
            {
                Tools = ImmutableArray.Create(new ToolDefinition(
                    exposedName,
                    tool.Description,
                    tool.JsonSchema)),
            };

            var chat = new ToolThenAnswerChatClient(exposedName);

            return new HarnessAcpSessionAgent(sessionId, client, chat, events, coreOptions, publishOptions, store, initialState, invoker);
        }
    }

    private sealed class ToolThenAnswerChatClient : MeaiIChatClient
    {
        private readonly string _toolName;
        private int _calls;

        public ToolThenAnswerChatClient(string toolName) => _toolName = toolName;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step1()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_1", _toolName, new Dictionary<string, object?> { ["message"] = "hi" })
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Ok.") },
                };
            }

            return _calls == 1 ? Step1() : Step2();
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }

    private sealed class SdkMcpToolInvoker : IMcpToolInvoker
    {
        private readonly IReadOnlyDictionary<string, McpClientTool> _tools;

        public SdkMcpToolInvoker(IReadOnlyDictionary<string, McpClientTool> tools) => _tools = tools;

        public bool CanInvoke(string toolName) => _tools.ContainsKey(toolName);

        public async Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        {
            var t = _tools[toolName];

            // Normalize args to JsonElement dict (MCP expects JsonElement values).
            var je = args is JsonElement e ? e : JsonSerializer.SerializeToElement(args);
            var dict = je.ValueKind == JsonValueKind.Object
                ? je.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => (object?)JsonSerializer.Deserialize<object?>(p.Value.GetRawText()))
                : new Dictionary<string, object?>();

            var result = await t.CallAsync(dict, cancellationToken: cancellationToken);

            // Return a stable JSON payload for committed events.
            // (MCP results are content blocks; we serialize the full result object.)
            return JsonSerializer.SerializeToElement(new
            {
                isError = result.IsError,
                structuredContent = result.StructuredContent,
                content = result.Content,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }

    private static class InMemoryMcp
    {
        public static async Task<McpClient> CreateEchoServerClientAsync()
        {
            var tool = new McpTool
            {
                Name = "echo",
                Description = "Echoes a message",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { message = new { type = "string" } },
                    required = new[] { "message" },
                }),
            };

            var server = new InMemoryMcpServer(
                tools: new List<McpTool> { tool },
                onCall: (name, arguments) =>
                {
                    var msg = arguments.TryGetValue("message", out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? ""
                        : "";

                    return new McpCallToolResult
                    {
                        Content = new List<McpContentBlock> { new McpTextContentBlock { Text = $"hello {msg}" } },
                        IsError = false,
                    };
                });

            var transport = new InMemoryClientTransport("fake", server);
            return await McpClient.CreateAsync(transport);
        }

        private sealed class InMemoryClientTransport : IClientTransport
        {
            private readonly InMemoryMcpServer _server;

            public InMemoryClientTransport(string name, InMemoryMcpServer server)
            {
                Name = name;
                _server = server;
            }

            public string Name { get; }

            public Task<McpITransport> ConnectAsync(CancellationToken cancellationToken = default)
            {
                var (client, server) = InMemoryDuplexTransport.CreatePair();
                _ = _server.RunAsync(server, cancellationToken);
                return Task.FromResult<McpITransport>(client);
            }
        }

        private sealed class InMemoryMcpServer
        {
            private readonly List<McpTool> _tools;
            private readonly Func<string, IDictionary<string, JsonElement>, McpCallToolResult> _onCall;

            public InMemoryMcpServer(List<McpTool> tools, Func<string, IDictionary<string, JsonElement>, McpCallToolResult> onCall)
            {
                _tools = tools;
                _onCall = onCall;
            }

            public async Task RunAsync(McpITransport transport, CancellationToken cancellationToken)
            {
                await foreach (var msg in transport.MessageReader.ReadAllAsync(cancellationToken))
                {
                    if (msg is ModelContextProtocol.Protocol.JsonRpcNotification n && n.Method == ModelContextProtocol.Protocol.NotificationMethods.InitializedNotification)
                        continue;

                    if (msg is not McpJsonRpcRequest req)
                        continue;

                    if (req.Method == McpRequestMethods.Initialize)
                    {
                        var result = new ModelContextProtocol.Protocol.InitializeResult
                        {
                            ProtocolVersion = "2024-11-05",
                            Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities { Tools = new ModelContextProtocol.Protocol.ToolsCapability() },
                            ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "inmem", Version = "0" },
                        };

                        await transport.SendMessageAsync(new McpJsonRpcResponse
                        {
                            Id = req.Id,
                            Result = JsonSerializer.SerializeToNode(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                        }, cancellationToken);
                        continue;
                    }

                    if (req.Method == McpRequestMethods.ToolsList)
                    {
                        var result = new McpListToolsResult { Tools = _tools };
                        await transport.SendMessageAsync(new McpJsonRpcResponse
                        {
                            Id = req.Id,
                            Result = JsonSerializer.SerializeToNode(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                        }, cancellationToken);
                        continue;
                    }

                    if (req.Method == McpRequestMethods.ToolsCall)
                    {
                        var p = req.Params.Deserialize<McpCallToolRequestParams>(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
                            ?? new McpCallToolRequestParams { Name = "" };
                        var r = _onCall(p.Name, p.Arguments ?? new Dictionary<string, JsonElement>());
                        await transport.SendMessageAsync(new McpJsonRpcResponse
                        {
                            Id = req.Id,
                            Result = JsonSerializer.SerializeToNode(r, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                        }, cancellationToken);
                        continue;
                    }

                    throw new InvalidOperationException($"Unexpected MCP method: {req.Method}");
                }
            }
        }

        private sealed class InMemoryDuplexTransport : McpITransport
        {
            private readonly ChannelReader<McpJsonRpcMessage> _reader;
            private readonly ChannelWriter<McpJsonRpcMessage> _writer;

            private InMemoryDuplexTransport(ChannelReader<McpJsonRpcMessage> reader, ChannelWriter<McpJsonRpcMessage> writer)
            {
                _reader = reader;
                _writer = writer;
            }

            public static (McpITransport client, McpITransport server) CreatePair()
            {
                var a = Channel.CreateUnbounded<McpJsonRpcMessage>();
                var b = Channel.CreateUnbounded<McpJsonRpcMessage>();

                var client = new InMemoryDuplexTransport(reader: a.Reader, writer: b.Writer);
                var server = new InMemoryDuplexTransport(reader: b.Reader, writer: a.Writer);

                return (client, server);
            }

            public string? SessionId => null;

            public ChannelReader<McpJsonRpcMessage> MessageReader => _reader;

            public Task SendMessageAsync(McpJsonRpcMessage message, CancellationToken cancellationToken = default)
                => _writer.WriteAsync(message, cancellationToken).AsTask();

            public ValueTask DisposeAsync()
            {
                _writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }
}
