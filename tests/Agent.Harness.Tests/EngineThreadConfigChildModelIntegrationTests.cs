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

public sealed class EngineThreadConfigChildModelIntegrationTests
{
    [Fact]
    public async Task ThreadConfig_can_set_child_model_and_thread_list_projects_it()
    {
        var sessionId = "ses_thread_config_child_model";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-thread-config-child-model", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new TwoPhaseScriptedChatClient();
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

        // Prompt 1: create child thread.
        var turn1 = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn1,
            cts.Token);

        var startRaw = turn1.CompletedRawOutputs.First(x => x.ToolName == "thread_start").RawOutput;
        var childId = ExtractThreadId(startRaw);
        childId.Should().NotBeNull();
        chat.ChildId = childId!;

        // Prompt 2: set child model via thread_config + then list.
        var turn2 = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = $"Configure child={childId}" } },
            },
            turn2,
            cts.Token);

        turn2.CompletedRawOutputs.Should().Contain(x => x.ToolName == "thread_config");
        turn2.CompletedRawOutputs.Should().Contain(x => x.ToolName == "thread_list");
        turn2.CompletedRawOutputs.Should().Contain(x => x.ToolName == "thread_config");

        var threadListRaw = turn2.CompletedRawOutputs.First(x => x.ToolName == "thread_list").RawOutput;
        threadListRaw.Should().NotBeNull();

        var childConfigRaw = turn2.CompletedRawOutputs.Last(x => x.ToolName == "thread_config").RawOutput;
        childConfigRaw.Should().NotBeNull();
        using var cfgDoc = JsonDocument.Parse(JsonSerializer.Serialize(childConfigRaw));
        cfgDoc.RootElement.GetProperty("threadId").GetString().Should().Be(childId);
        cfgDoc.RootElement.GetProperty("model").GetString().Should().Be("m2");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(threadListRaw));
        var threads = doc.RootElement.GetProperty("threads").EnumerateArray().ToList();

        static string? GetString(JsonElement el, string a, string b)
        {
            if (el.TryGetProperty(a, out var pa) && pa.ValueKind == JsonValueKind.String) return pa.GetString();
            if (el.TryGetProperty(b, out var pb) && pb.ValueKind == JsonValueKind.String) return pb.GetString();
            return null;
        }

        var child = threads.Single(t => GetString(t, "threadId", "ThreadId") == childId);
        GetString(child, "model", "Model").Should().Be("m2");

        var main = threads.Single(t => GetString(t, "threadId", "ThreadId") == "main");
        GetString(main, "model", "Model").Should().Be("default");
    }

    private static string? ExtractThreadId(object? rawOutput)
    {
        if (rawOutput is null) return null;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(rawOutput));
        if (doc.RootElement.TryGetProperty("threadId", out var tid) && tid.ValueKind == JsonValueKind.String)
            return tid.GetString();
        return null;
    }

    private sealed class TwoPhaseScriptedChatClient : MeaiIChatClient
    {
        private bool _started;
        private bool _configured;

        public string? ChildId { get; set; }

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            // One PromptAsync may cause multiple model calls (main + child), so gate on test-driven state.
            if (!_started)
            {
                _started = true;
                return TurnStartChild();
            }

            if (!_configured && !string.IsNullOrWhiteSpace(ChildId))
            {
                _configured = true;
                return TurnConfigureChild();
            }

            return Text("ok");

            async IAsyncEnumerable<MeaiChatResponseUpdate> TurnStartChild()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_start_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "start child" }),
                        new MeaiFunctionCallContent("call_start_1", "thread_start", new Dictionary<string, object?>
                        {
                            ["name"] = "child",
                            ["context"] = "fork",
                            ["message"] = "do work",
                            ["delivery"] = "immediate",
                        }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> TurnConfigureChild()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_cfg_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "set child model" }),
                        new MeaiFunctionCallContent("call_cfg_1", "thread_config", new Dictionary<string, object?> { ["threadId"] = ChildId, ["model"] = "m2" }),
                        new MeaiFunctionCallContent("call_cfg_2", "thread_list", new Dictionary<string, object?>()),
                        new MeaiFunctionCallContent("call_cfg_3", "thread_config", new Dictionary<string, object?> { ["threadId"] = ChildId }),
                    }
                };
                await Task.CompletedTask;
            }

            static async IAsyncEnumerable<MeaiChatResponseUpdate> Text(string text)
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent(text) } };
                await Task.CompletedTask;
            }
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
