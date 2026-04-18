using System.Text.Json;
using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
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

public sealed class AcpChildThreadIdleNotificationTriggersParentModelContinuationTests
{
    [Fact]
    public async Task ChildIdleNotification_WakesParent_AndCallsModel()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var factory = new Factory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        // We don't need to handle client->agent requests in this test.
        client.RequestHandler = (req, _) => throw new InvalidOperationException($"Unexpected request: {req.Method}");

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities() },
            },
            cts.Token);

        var ses = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = ses.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            cts.Token);

        // The parent should have continued after receiving the child idle notification.
        factory.Chat.MainContinuationCount.Should().BeGreaterThanOrEqualTo(1);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public ScriptedChatClient Chat { get; } = new();

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_idle_cont", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "harness-acp-idle-cont-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(rootDir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            // Ensure thread tools exist so the main model can create a child.
            var initialState = SessionState.Empty with
            {
                Tools = ImmutableArray.Create(
                    ToolSchemas.ReportIntent,
                    ToolSchemas.ThreadList,
                    ToolSchemas.ThreadStart,
                    ToolSchemas.ThreadSend,
                    ToolSchemas.ThreadRead),
            };

            return new HarnessAcpSessionAgent(sessionId, client, Chat, _ => Chat, "default", events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedChatClient : MeaiIChatClient
    {
        private bool _mainToolsDone;
        private bool _childToolsDone;
        public int MainContinuationCount { get; private set; }

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            static string Render(MeaiChatMessage m)
            {
                if (!string.IsNullOrEmpty(m.Text)) return m.Text!;
                if (m.Contents is null) return string.Empty;

                return string.Join("\n", m.Contents.Select(c => c switch
                {
                    MeaiTextContent t => t.Text ?? string.Empty,
                    MeaiFunctionCallContent fc => $"<tool_call name=\"{fc.Name}\"/>",
                    _ => c.ToString() ?? string.Empty,
                }));
            }

            var msgText = string.Join("\n", messages.Select(Render));

            bool isChildPrompt = msgText.Contains("<inter_thread", StringComparison.Ordinal) && msgText.Contains("do work", StringComparison.Ordinal);
            bool isMainIdleWake = msgText.Contains("<thread_idle", StringComparison.Ordinal);
            bool isMainPrompt = !isChildPrompt && !isMainIdleWake && msgText.Contains("Hi", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Tools_CreateChild()
            {
                _mainToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "spawn" }),
                        new MeaiFunctionCallContent("call_m_1", "thread_start", new Dictionary<string, object?> { ["context"] = "fork", ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Text_InitialDone()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ok") } };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Text_ContinueAfterIdle()
            {
                MainContinuationCount++;
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("continued") } };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Tools_ReportIntent()
            {
                _childToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_c_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "child" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text_Result()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") } };
            }

            if (isChildPrompt)
                return !_childToolsDone ? Child_Tools_ReportIntent() : Child_Text_Result();

            if (isMainIdleWake)
                return Main_Text_ContinueAfterIdle();

            if (isMainPrompt)
                return !_mainToolsDone ? Main_Tools_CreateChild() : Main_Text_InitialDone();

            throw new InvalidOperationException($"Unexpected prompt messages: {msgText}");
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
