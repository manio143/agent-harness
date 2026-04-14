using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using System.Collections.Immutable;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class AcpToolKindPublishingIntegrationTests
{
    [Theory]
    [InlineData("read_text_file", "read")]
    [InlineData("write_text_file", "edit")]
    public async Task SessionPrompt_WhenToolStarts_PublishesExpectedToolKind(string toolName, string expectedKind)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory(toolName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<SessionUpdate>();
        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update")
                return;

            var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
            if (payload is not null)
                updates.Add(payload.Update);
        };

        // Respond to agent->client tool requests.
        client.RequestHandler = (req, _) =>
        {
            if (req.Method == "fs/read_text_file")
            {
                var resp = new ReadTextFileResponse { Content = "hello" };
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(resp, AcpJson.Options));
                return Task.FromResult(doc.RootElement.Clone());
            }

            if (req.Method == "fs/write_text_file")
            {
                var resp = new WriteTextFileResponse();
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(resp, AcpJson.Options));
                return Task.FromResult(doc.RootElement.Clone());
            }

            throw new InvalidOperationException($"Unexpected request: {req.Method}");
        };

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities
                {
                    Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
                    Terminal = false,
                },
            },
            cts.Token);

        var newSession = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Run a tool" } },
            },
            cts.Token);

        // Assert: the initial tool_call update has the expected kind.
        // ToolCallTracker emits updates fire-and-forget, so wait briefly for notifications to arrive.
        Agent.Acp.Schema.ToolCall? start = null;
        for (var i = 0; i < 50 && start is null; i++)
        {
            start = updates.OfType<Agent.Acp.Schema.ToolCall>()
                .FirstOrDefault(u => u.ToolCallId == "call_1" && u.Status.Value == ToolCallStatus.Pending);

            if (start is null)
                await Task.Delay(20, cts.Token);
        }

        start.Should().NotBeNull();
        start!.Title.Should().Be(toolName);
        start.Kind.Value.Should().Be(expectedKind);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory(string toolName) : IAcpAgentFactory
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
            var dir = Path.Combine(Path.GetTempPath(), "harness-acp-toolkind-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(dir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions();
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            var chat = new ScriptedMeaiChatClient(toolName);

            var initialState = SessionState.Empty with
            {
                Tools = toolName == "write_text_file"
                    ? ImmutableArray.Create(ToolSchemas.WriteTextFile)
                    : ImmutableArray.Create(ToolSchemas.ReadTextFile),
            };

            return new HarnessAcpSessionAgent(sessionId, client, chat, events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedMeaiChatClient(string toolName) : MeaiIChatClient
    {
        private int _calls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step1()
            {
                var args = toolName switch
                {
                    "read_text_file" => new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" },
                    "write_text_file" => new Dictionary<string, object?> { ["path"] = "/tmp/a.txt", ["content"] = "hi" },
                    _ => new Dictionary<string, object?>(),
                };

                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_1", toolName, args)
                    }
                };
                yield break;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiTextContent("Done."),
                    }
                };

                yield break;
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
}
