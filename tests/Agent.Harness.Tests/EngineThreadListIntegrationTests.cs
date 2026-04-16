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

/// <summary>
/// Engine-seam migration of <see cref="AcpThreadListIntegrationTests"/>.
/// Asserts thread_list reflects report_intent for main thread without going through JSON-RPC session/update.
/// </summary>
public sealed class EngineThreadListIntegrationTests
{
    [Fact]
    public async Task ThreadList_Reflects_ReportIntent_ForMainThread()
    {
        var sessionId = "ses_thread_list";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-thread-tests", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var chat = new ScriptedMeaiChatClient();
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

        var turn = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();

        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn,
            cts.Token);

        var threadListRaw = turn.CompletedRawOutputs.FirstOrDefault(x => x.ToolName == "thread_list").RawOutput;
        threadListRaw.Should().NotBeNull("expected thread_list rawOutput");

        var raw = JsonSerializer.Serialize(threadListRaw);
        using var doc = JsonDocument.Parse(raw);

        var threads = doc.RootElement.GetProperty("threads");
        threads.ValueKind.Should().Be(JsonValueKind.Array);

        static string? GetString(JsonElement el, string a, string b)
        {
            if (el.TryGetProperty(a, out var pa) && pa.ValueKind == JsonValueKind.String) return pa.GetString();
            if (el.TryGetProperty(b, out var pb) && pb.ValueKind == JsonValueKind.String) return pb.GetString();
            return null;
        }

        var main = threads.EnumerateArray().First(t => GetString(t, "threadId", "ThreadId") == "main");
        GetString(main, "intent", "Intent").Should().Be("list threads");
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
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
                        new MeaiFunctionCallContent($"call_{_calls}_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "list threads" }),
                        new MeaiFunctionCallContent($"call_{_calls}_1", "thread_list", new Dictionary<string, object?>()),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Done.") },
                };
                await Task.CompletedTask;
            }

            return _calls == 1 ? Step1() : Step2();
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
