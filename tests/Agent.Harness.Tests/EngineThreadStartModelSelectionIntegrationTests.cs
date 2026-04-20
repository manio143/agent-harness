using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
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

public sealed class EngineThreadStartModelSelectionIntegrationTests
{
    [Fact]
    public async Task ThreadStart_with_model_routes_child_first_model_call_via_chatByModel()
    {
        var sessionId = "ses_thread_start_model";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-thread-start-model", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var mainChat = new MainScriptedChatClient();
        var m2Chat = new RecordingTextChatClient("child ok");

        MeaiIChatClient ChatByModel(string model)
            => string.Equals(model, "m2", StringComparison.OrdinalIgnoreCase) ? m2Chat : mainChat;

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullClientCaller(),
            chat: mainChat,
            chatByModel: ChatByModel,
            quickWorkModel: "default",
            events: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: Agent.Harness.SessionState.Empty,
            mcp: NullMcpToolInvoker.Instance,
            isKnownModel: m => string.Equals(m, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(m, "m2", StringComparison.OrdinalIgnoreCase));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var turn = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();

        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn,
            cts.Token);

        // Sanity: the tool call should have completed.
        turn.CompletedRawOutputs.Should().Contain(x => x.ToolName == "report_intent", $"tool outputs: {string.Join(",", turn.CompletedRawOutputs.Select(x => x.ToolName))}");

        var startRaw = turn.CompletedRawOutputs.FirstOrDefault(x => x.ToolName == "thread_start").RawOutput;
        startRaw.Should().NotBeNull($"expected thread_start rawOutput. tool outputs: {string.Join(",", turn.CompletedRawOutputs.Select(x => x.ToolName))}");

        // The child should have executed at least one model call using chatByModel("m2").
        m2Chat.Calls.Should().BeGreaterThan(0, $"child thread should call the model using the configured model (mainChatCalls={mainChat.Calls}, startRaw={JsonSerializer.Serialize(startRaw)})");
        m2Chat.LastText.Should().Contain("do work");
    }

    private sealed class MainScriptedChatClient : MeaiIChatClient
    {
        public int Calls => _calls;
        private int _calls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step1()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent($"call_{_calls}_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "start child" }),
                        new MeaiFunctionCallContent($"call_{_calls}_1", "thread_start", new Dictionary<string, object?>
                        {
                            ["name"] = "child",
                            ["context"] = "new",
                            ["model"] = "m2",
                            ["message"] = "do work",
                            ["delivery"] = "immediate",
                        }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2_TextOnly()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Done.") },
                };
                await Task.CompletedTask;
            }

            return _calls == 1 ? Step1() : Step2_TextOnly();
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingTextChatClient(string responseText) : MeaiIChatClient
    {
        public int Calls { get; private set; }
        public string LastText { get; private set; } = string.Empty;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastText = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));

            return OneShot();

            async IAsyncEnumerable<MeaiChatResponseUpdate> OneShot()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent(responseText) },
                };
                await Task.CompletedTask;
            }
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
