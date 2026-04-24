using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
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
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class ThreadCapabilitiesOfferFilteringIntegrationTests
{
    [Fact]
    public async Task ChildThread_DenyThreadsCapability_IsNotOfferedThreadTools()
    {
        var sessionId = "ses_caps_offer_filter";
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-caps-offer-filter-tests", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new InspectingScriptedChatClient();

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
            initialState: Agent.Harness.SessionState.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var turn = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn,
            cts.Token);

        chat.SawChildPrompt.Should().BeTrue("expected child thread to be prompted");
        chat.ChildOfferedToolNames.Should().NotContain(n => n.StartsWith("thread_", StringComparison.Ordinal));
        chat.ChildOfferedToolNames.Should().Contain("report_intent");
    }

    private sealed class InspectingScriptedChatClient : MeaiIChatClient
    {
        private bool _mainToolsDone;
        private bool _mainTextDone;
        private bool _childTextDone;

        public bool SawChildPrompt { get; private set; }
        public ImmutableArray<string> ChildOfferedToolNames { get; private set; } = ImmutableArray<string>.Empty;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            static string Render(MeaiChatMessage m)
            {
                if (!string.IsNullOrEmpty(m.Text)) return m.Text!;
                if (m.Contents is null) return string.Empty;

                return string.Join("\n", m.Contents.Select(c => c switch
                {
                    MeaiTextContent t => t.Text ?? string.Empty,
                    MeaiFunctionCallContent fc => $"<tool_call name=\"{fc.Name}\"/>",
                    MeaiFunctionResultContent fr => JsonSerializer.Serialize(fr.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    _ => c.ToString() ?? string.Empty,
                }));
            }

            var rendered = string.Join("\n", messages.Select(Render));

            bool isChild = rendered.Contains("<thread_created", StringComparison.Ordinal) && rendered.Contains("<task>", StringComparison.Ordinal);
            bool isMain = rendered.Contains("\nHi", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Tools_CreateRestrictedChild()
            {
                _mainToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_m_1", "thread_start", new Dictionary<string, object?>
                        {
                            ["name"] = "child",
                            ["context"] = "new",
                            ["mode"] = "single",
                            ["delivery"] = "immediate",
                            ["message"] = "do work",
                            ["capabilities"] = new Dictionary<string, object?>
                            {
                                ["deny"] = new [] { "threads" }
                            }
                        }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Text_Done()
            {
                _mainTextDone = true;
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("OK") } };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text_Run()
            {
                SawChildPrompt = true;
                ChildOfferedToolNames = (options?.Tools is null
                        ? ImmutableArray<string>.Empty
                        : options.Tools.Select(t => t.Name).ToImmutableArray());

                _childTextDone = true;
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") } };
                await Task.CompletedTask;
            }

            if (isChild)
                return !_childTextDone ? Child_Text_Run() : AsyncEmpty();

            if (isMain)
            {
                if (!_mainToolsDone)
                    return Main_Tools_CreateRestrictedChild();
                if (!_mainTextDone)
                    return Main_Text_Done();

                return AsyncEmpty();
            }

            // tolerate any idle/notification wake-ups
            return Main_Text_Done();
        }

        private static async IAsyncEnumerable<MeaiChatResponseUpdate> AsyncEmpty()
        {
            yield break;
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
