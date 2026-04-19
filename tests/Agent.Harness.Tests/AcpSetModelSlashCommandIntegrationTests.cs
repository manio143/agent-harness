using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Agent.Harness.Tests;

public sealed class AcpSetModelSlashCommandIntegrationTests
{
    [Fact]
    public async Task Prompt_WhenSetModelSlashCommand_CommitsSetModel_AndDoesNotCallChatClient()
    {
        var sessionId = "ses_set_model_slash";
        var root = Path.Combine(Path.GetTempPath(), "harness-set-model-slash", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new RecordingMeaiChatClient();

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
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
            new Agent.Acp.Schema.PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "/set-model m2" } },
            },
            turn,
            cts.Token);

        chat.CallCount.Should().Be(0);

        var threadStore = new Agent.Harness.Threads.JsonlThreadStore(root);
        var committed = threadStore.LoadCommittedEvents(sessionId, Agent.Harness.Threads.ThreadIds.Main);
        committed.OfType<Agent.Harness.SetModel>().Should().ContainSingle(m => m.Model == "m2");
    }

    [Fact]
    public async Task Prompt_WhenModelCatalogSystemPromptProvided_IncludesItInFirstSystemMessage()
    {
        var sessionId = "ses_model_catalog_prompt";
        var root = Path.Combine(Path.GetTempPath(), "harness-model-catalog-prompt", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new RecordingMeaiChatClient
        {
            // Minimal response: one assistant message.
            Script = _ => new[] { new MeaiChatResponseUpdate() },
        };

        var prompt = "Available inference models: default, m2. Default: default. Quick-work: default.";

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: Agent.Harness.SessionState.Empty,
            mcp: NullMcpToolInvoker.Instance,
            isKnownModel: _ => true,
            modelCatalogSystemPrompt: prompt);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var turn = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();

        _ = await agent.PromptAsync(
            new Agent.Acp.Schema.PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn,
            cts.Token);

        chat.CallCount.Should().BeGreaterThan(0);
        chat.LastMessages.Should().NotBeNull();

        chat.LastMessages!.First().Role.Should().Be(Microsoft.Extensions.AI.ChatRole.System);
        chat.LastMessages!.First().Text.Should().Be(prompt);
    }

    private sealed class RecordingMeaiChatClient : MeaiIChatClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<MeaiChatMessage>? LastMessages { get; private set; }

        public Func<IReadOnlyList<MeaiChatMessage>, IEnumerable<MeaiChatResponseUpdate>>? Script { get; init; }

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = messages.ToList();

            async IAsyncEnumerable<MeaiChatResponseUpdate> Run()
            {
                var updates = Script?.Invoke(LastMessages) ?? Array.Empty<MeaiChatResponseUpdate>();
                foreach (var u in updates)
                    yield return u;
                await Task.CompletedTask;
            }

            return Run();
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
