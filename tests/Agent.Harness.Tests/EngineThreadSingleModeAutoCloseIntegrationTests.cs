using System.Text.Json;
using System.Text.RegularExpressions;
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

public sealed class EngineThreadSingleModeAutoCloseIntegrationTests
{
    [Fact]
    public async Task SingleModeChildBecomesIdle_ThenIsClosed_AndDisappearsFromThreadList()
    {
        var sessionId = "ses_single_mode_autoclose";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-single-mode-autoclose-tests", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threadStore = new JsonlThreadStore(root);
        var chat = new ScriptedMeaiChatClient();

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

        var turn1 = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn1,
            cts.Token);

        var childId = turn1.CompletedRawOutputs
            .Where(x => x.ToolName == "thread_start")
            .Select(x => ExtractThreadId(x.RawOutput))
            .FirstOrDefault(x => x is not null);

        childId.Should().NotBeNullOrWhiteSpace();

        // Prompt 2 asks for thread_list. Single-mode child should have auto-closed after reaching idle.
        var turn2 = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "List threads" } },
            },
            turn2,
            cts.Token);

        var listOut = turn2.CompletedRawOutputs.Single(x => x.ToolName == "thread_list").RawOutput;
        listOut.Should().NotBeNull();

        var json = JsonSerializer.Serialize(listOut);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("threads", out var threads).Should().BeTrue("expected thread_list output to contain 'threads' property, got: {0}", json);

        var ids = threads.EnumerateArray()
            .Select(t => t.TryGetProperty("threadId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        ids.Should().NotContain(childId);

        // Sanity check: metadata is actually marked closed.
        var meta = threadStore.TryLoadThreadMetadata(sessionId, childId!);
        meta.Should().NotBeNull();
        meta!.ClosedAtIso.Should().NotBeNullOrWhiteSpace();
        meta.ClosedReason.Should().Be("completed");
    }

    private static string? ExtractThreadId(object? rawOutput)
    {
        if (rawOutput is null) return null;
        var json = JsonSerializer.Serialize(rawOutput);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("threadId", out var tid) && tid.ValueKind == JsonValueKind.String)
            return tid.GetString();
        return null;
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private bool _main1ToolsDone;
        private bool _main2ToolsDone;
        private bool _childDone;

        private static readonly Regex ChildIdRe = new("\\u0022threadId\\u0022:\\u0022([A-Za-z0-9_-]+)\\u0022", RegexOptions.Compiled);

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
                    MeaiFunctionResultContent fr => JsonSerializer.Serialize(fr.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    _ => c.ToString() ?? string.Empty,
                }));
            }

            var rendered = string.Join("\n", messages.Select(Render));

            bool isChild = rendered.Contains("<thread_created", StringComparison.Ordinal) && rendered.Contains("<task>do work</task>", StringComparison.Ordinal);
            bool isMain1 = rendered.Contains("\nHi", StringComparison.Ordinal);
            bool isMain2 = rendered.Contains("\nList threads", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Tools_CreateChild()
            {
                _main1ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m1_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_m1_1", "thread_start", new Dictionary<string, object?> { ["name"] = "child", ["context"] = "new", ["mode"] = "single", ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Text_Done()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("Created") } };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text_Run()
            {
                _childDone = true;
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") } };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Tools_List()
            {
                _main2ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "list" }),
                        new MeaiFunctionCallContent("call_m2_1", "thread_list", new Dictionary<string, object?>()),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Text_Done()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("Done") } };
                await Task.CompletedTask;
            }

            if (isChild)
                return !_childDone ? Child_Text_Run() : AsyncEmpty();

            if (isMain2)
                return !_main2ToolsDone ? Main2_Tools_List() : Main2_Text_Done();

            if (isMain1)
                return !_main1ToolsDone ? Main1_Tools_CreateChild() : Main1_Text_Done();

            // After the child becomes idle, the parent may be woken with a system idle notification.
            // Just respond with a benign completion.
            if (rendered.Contains("<thread_idle", StringComparison.Ordinal))
                return Main1_Text_Done();

            // Also tolerate unexpected continuation prompts that include the threadId.
            if (ChildIdRe.IsMatch(rendered))
                return Main1_Text_Done();

            throw new InvalidOperationException($"Unexpected prompt. Rendered=\n{rendered}");
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
