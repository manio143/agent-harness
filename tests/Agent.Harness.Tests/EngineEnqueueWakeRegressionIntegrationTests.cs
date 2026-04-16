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

/// <summary>
/// Engine-seam migration of <see cref="AcpEnqueueWakeRegressionIntegrationTests"/>.
/// Validates that enqueue delivery schedules a follow-up model call without requiring a second ACP prompt.
/// </summary>
public sealed class EngineEnqueueWakeRegressionIntegrationTests
{
    [Fact]
    public async Task EnqueueDelivery_SchedulesFollowupModelCall_WithoutSecondPrompt()
    {
        var sessionId = "ses_enqueue_reg";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-enqueue-tests", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var events = new RecordingSessionEvents();
        var chat = new ScriptedMeaiChatClient();

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullClientCaller(),
            chat,
            events,
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store,
            initialState: Agent.Harness.SessionState.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullPromptTurn(),
            cts.Token);

        // Follow-up model call should have happened within the same PromptAsync drain-to-quiescence.
        events.AgentMessageChunks.Should().Contain("Followup");
    }

    private sealed class RecordingSessionEvents : IAcpSessionEvents
    {
        public List<string> AgentMessageChunks { get; } = new();

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            if (update is AgentMessageChunk chunk && chunk.Content is TextContent t)
                AgentMessageChunks.Add(t.Text);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private int _promptCalls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            // Distinguish warmup from real prompt/wake calls.
            var isPromptish = messages.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.User);
            if (!isPromptish)
                return Warmup();

            _promptCalls++;

            return _promptCalls switch
            {
                1 => Call1_Enqueue(),
                2 => Call2_Tools(),
                _ => Call3_Text(),
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Warmup()
        {
            yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("(warmup)") } };
            await Task.CompletedTask;
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call1_Enqueue()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("callA0", "report_intent", new Dictionary<string, object?> { ["intent"] = "enqueue" }),
                    new MeaiFunctionCallContent("callA1", "thread_send", new Dictionary<string, object?> { ["threadId"] = "main", ["message"] = "wake", ["delivery"] = "enqueue" }),
                }
            };

            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent> { new MeaiTextContent(".") }
            };

            await Task.CompletedTask;
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call2_Tools()
        {
            // Follow-up wake: choose an intent and list threads.
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("callB0", "report_intent", new Dictionary<string, object?> { ["intent"] = "process" }),
                    new MeaiFunctionCallContent("callB1", "thread_list", new Dictionary<string, object?>()),
                }
            };

            await Task.CompletedTask;
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call3_Text()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent> { new MeaiTextContent("Followup") }
            };

            await Task.CompletedTask;
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
