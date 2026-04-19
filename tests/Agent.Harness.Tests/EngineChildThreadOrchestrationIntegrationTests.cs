using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Engine-seam migration of the old ACP JSON-RPC integration test.
/// Drives <see cref="HarnessAcpSessionAgent"/> directly (no in-memory JSON-RPC server/client)
/// and asserts using tool raw outputs + committed thread log on disk.
/// </summary>
public sealed class EngineChildThreadOrchestrationIntegrationTests
{
    [Fact]
    public async Task ThreadStart_fork_Immediate_RunsChild_AndThreadReadReturnsChildAssistantMessages()
    {
        var sessionId = "ses_child_orch";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-child-orch-tests", Guid.NewGuid().ToString("N"));

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
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: Agent.Harness.SessionState.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Prompt 1: model creates a child thread with immediate delivery. Child should run via scheduler.
        var turn1 = new RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn1,
            cts.Token);

        // Wait briefly for child orchestration scheduling.
        await Task.Delay(250, cts.Token);

        var childId = turn1.CompletedRawOutputs
            .Where(x => x.ToolName == "thread_start")
            .Select(x => ExtractThreadId(x.RawOutput))
            .FirstOrDefault(x => x is not null);

        childId.Should().NotBeNull("expected thread_start to return a threadId rawOutput");
        childId!.Should().StartWith("thr_");

        var childEventsPath = Path.Combine(root, sessionId, "threads", childId!, "events.jsonl");

        // Prompt 2: ask the agent to read the child thread.
        var turn2 = new RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = $"Check child={childId}" } },
            },
            turn2,
            cts.Token);

        // Ensure we actually wrote something for the child.
        File.Exists(childEventsPath).Should().BeTrue($"expected child events at {childEventsPath}");
        var childEvents = await File.ReadAllTextAsync(childEventsPath, cts.Token);
        childEvents.Should().Contain("ChildResult", $"child events were: {childEvents}");

        // Assert thread_read result contains the child assistant message.
        var threadReadRaw = turn2.CompletedRawOutputs.FirstOrDefault(x => x.ToolName == "thread_read").RawOutput;
        threadReadRaw.Should().NotBeNull("expected thread_read to complete with rawOutput");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(threadReadRaw));
        doc.RootElement.TryGetProperty("messages", out var messages).Should().BeTrue();
        messages.ValueKind.Should().Be(JsonValueKind.Array);

        static string? GetText(JsonElement el)
        {
            if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
            if (el.TryGetProperty("Text", out var T) && T.ValueKind == JsonValueKind.String) return T.GetString();
            return null;
        }

        messages.EnumerateArray().Select(GetText).Should().Contain("ChildResult");
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

    internal sealed class RecordingPromptTurn : IAcpPromptTurn
    {
        public IAcpToolCalls ToolCalls { get; }

        public List<(string ToolName, object? RawOutput)> CompletedRawOutputs { get; } = new();

        public RecordingPromptTurn()
        {
            ToolCalls = new RecordingToolCalls(CompletedRawOutputs);
        }

        private sealed class RecordingToolCalls(List<(string ToolName, object? RawOutput)> completed) : IAcpToolCalls
        {
            private readonly HashSet<string> _active = new();
            public IReadOnlyCollection<string> ActiveToolCallIds => _active;

            public IAcpToolCall Start(string toolCallId, string title, ToolKind kind)
            {
                _active.Add(toolCallId);
                return new RecordingToolCall(toolCallId, title, completed, () => _active.Remove(toolCallId));
            }

            public Task CancelAllAsync(CancellationToken cancellationToken = default)
            {
                _active.Clear();
                return Task.CompletedTask;
            }

            private sealed class RecordingToolCall(
                string toolCallId,
                string title,
                List<(string ToolName, object? RawOutput)> completed,
                Action onTerminal) : IAcpToolCall
            {
                public string ToolCallId { get; } = toolCallId;

                public Task AddContentAsync(ToolCallContent content, CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task InProgressAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

                public Task CompletedAsync(CancellationToken cancellationToken = default, object? rawOutput = null)
                {
                    completed.Add((ToolName: title, RawOutput: rawOutput));
                    onTerminal();
                    return Task.CompletedTask;
                }

                public Task FailedAsync(string message, CancellationToken cancellationToken = default)
                {
                    onTerminal();
                    return Task.CompletedTask;
                }

                public Task CancelledAsync(CancellationToken cancellationToken = default)
                {
                    onTerminal();
                    return Task.CompletedTask;
                }
            }
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private bool _main1ToolsDone;
        private bool _main2ToolsDone;
        private bool _childToolsDone;

        private static readonly Regex ChildIdRe = new("thr_[a-f0-9]{12}", RegexOptions.Compiled);

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

            bool isMainPrompt2 = msgText.Contains("Check child=", StringComparison.Ordinal);
            bool isChildPrompt = msgText.Contains("<thread_created", StringComparison.Ordinal) && msgText.Contains("do work", StringComparison.Ordinal);

            // After child becomes idle, the parent may receive a <thread_idle .../> system message and
            // be woken automatically (wake is an effect). Treat this as a follow-up main prompt.
            bool isMainPromptIdle = msgText.Contains("<thread_idle", StringComparison.Ordinal);

            bool isMainPrompt1 = !isMainPrompt2 && !isMainPromptIdle && msgText.Contains("\nHi", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Tools_CreateChild()
            {
                _main1ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m1_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_m1_1", "thread_start", new Dictionary<string, object?> { ["context"] = "fork", ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Created.") },
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child1_Tools_ListThreads()
            {
                _childToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_c_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "child work" }),
                        new MeaiTextContent("ChildResult"),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Tools_ReadChild(string childId)
            {
                _main2ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "read child" }),
                        new MeaiFunctionCallContent("call_m2_1", "thread_read", new Dictionary<string, object?> { ["threadId"] = childId }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Checked.") },
                };
                await Task.CompletedTask;
            }

            if (isChildPrompt)
                return !_childToolsDone ? Child1_Tools_ListThreads() : Main2_Text_Done();

            if (isMainPrompt2)
            {
                var m = ChildIdRe.Match(msgText);
                m.Success.Should().BeTrue("expected main prompt 2 to contain a child thread id; prompt was: {0}", msgText);
                return !_main2ToolsDone ? Main2_Tools_ReadChild(m.Value) : Main2_Text_Done();
            }

            if (isMainPrompt1)
                return !_main1ToolsDone ? Main1_Tools_CreateChild() : Main1_Text_Done();

            if (isMainPromptIdle)
                return Main1_Text_Done();

            throw new InvalidOperationException($"Unexpected prompt. Rendered=\n{msgText}");
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
