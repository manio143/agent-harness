using System.Collections.Immutable;
using Agent.Acp.Schema;
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

public sealed class AcpThreadNewDoesNotDeadlockIntegrationTests
{
    [Fact]
    public async Task ThreadNew_DoesNotDeadlock_WhenInvokedDuringPrompt()
    {
        var sessionId = "ses_thread_new_deadlock";
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-new-deadlock", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new ThreadNewChatClient();
        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullClientCaller(),
            chat,
            events: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store,
            initialState: Agent.Harness.SessionState.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullPromptTurn(),
            cts.Token);

        // Assert: child thread was created.
        var threadStore = new JsonlThreadStore(root);
        threadStore.ListThreads(sessionId).Should().Contain(t => t.ParentThreadId == ThreadIds.Main && t.ThreadId != ThreadIds.Main);
    }

    private sealed class ThreadNewChatClient : MeaiIChatClient
    {
        private bool _toolsDone;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!_toolsDone)
            {
                _toolsDone = true;
                return Tools();
            }

            return Text();

            async IAsyncEnumerable<MeaiChatResponseUpdate> Tools()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "thread new" }),
                        new MeaiFunctionCallContent("call_1", "thread_new", new Dictionary<string, object?>
                        {
                            ["message"] = "child hello",
                            ["delivery"] = "immediate",
                        }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Text()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ok") } };
                await Task.CompletedTask;
            }
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
