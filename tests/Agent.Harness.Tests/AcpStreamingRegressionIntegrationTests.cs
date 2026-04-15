using System.Text.Json;
using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.TitleGeneration;
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

public sealed class AcpStreamingRegressionIntegrationTests
{
    [Fact]
    public async Task SessionPrompt_StreamsAgentMessageChunks_Before_ToolCallUpdates()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var ordered = new List<string>();

        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update")
                return;

            var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
            if (payload is null) return;

            switch (payload.Update)
            {
                case AgentMessageChunk mc:
                    ordered.Add($"agent_chunk:{((Agent.Acp.Schema.TextContent)mc.Content).Text}");
                    break;

                case Agent.Acp.Schema.ToolCall tc:
                    // Pending is the first state we publish on tool start.
                    if (tc.Status.Value == ToolCallStatus.Pending)
                        ordered.Add($"tool_pending:{tc.ToolCallId}");
                    break;
            }
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
                    Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = false },
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
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Hi" } },
            },
            cts.Token);

        // Assert order: we must stream message chunks before any tool-call start is published.
        // The first model call emits two text chunks ("Hel" then "lo") before tool intent.
        ordered.Should().ContainInOrder(
            "agent_chunk:Hel",
            "agent_chunk:lo",
            "tool_pending:call_1");

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
            var dir = Path.Combine(Path.GetTempPath(), "harness-acp-streaming-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(dir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            var chat = new ScriptedMeaiChatClient();

            var initialState = SessionState.Empty with
            {
                Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
            };

            // Title generation unused here.
            return new HarnessAcpSessionAgent(sessionId, client, chat, events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private int _calls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step1()
            {
                // Stream some assistant text first.
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Hel") },
                };

                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("lo") },
                };

                // Then emit tool calls in the SAME model response.
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "read file" }),
                        new MeaiFunctionCallContent("call_1", "read_text_file", new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2()
            {
                // Second model call produces the final assistant completion (not important for this regression).
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Done.") },
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
}
