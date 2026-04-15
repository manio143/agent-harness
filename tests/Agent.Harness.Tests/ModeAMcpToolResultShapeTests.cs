using System.Collections.Immutable;
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

public sealed class ModeAMcpToolResultShapeTests
{
    [Fact]
    public async Task ModeA_WhenMcpToolCompletes_CommitsStableResultShape()
    {
        var store = new InMemorySessionStore();
        store.CreateNew("s1", new SessionMetadata(
            SessionId: "s1",
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:00Z"));

        var toolName = "fake_server__echo";
        var tool = new ToolDefinition(toolName, "", JsonSerializer.SerializeToElement(new { type = "object" }));

        var expected = JsonSerializer.SerializeToElement(new
        {
            isError = false,
            structuredContent = new { answer = 123 },
            content = new[] { new { type = "text", text = "hello hi" } },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var invoker = new FixedMcpInvoker(toolName, expected);
        var chat = new ToolThenAnswerChatClient(toolName);

        var agent = new HarnessAcpSessionAgent(
            sessionId: "s1",
            client: new NullClientCaller(),
            chat: chat,
            events: new NullSessionEvents(),
            coreOptions: new CoreOptions(),
            publishOptions: new AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, tool) },
            mcp: invoker,
            logLlmPrompts: false,
            logObservedEvents: false);

        var resp = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = "s1",
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Call MCP" } },
            },
            new NullPromptTurn(),
            CancellationToken.None);

        resp.StopReason.Value.Should().Be(StopReason.EndTurn);

        var committed = store.LoadCommitted("s1");
        var completed = committed.OfType<ToolCallCompleted>().Single(c => c.ToolId == "call_1");

        completed.Result.TryGetProperty("isError", out var isError).Should().BeTrue();
        isError.ValueKind.Should().Be(JsonValueKind.False);

        completed.Result.TryGetProperty("structuredContent", out var structured).Should().BeTrue();
        structured.GetRawText().Should().Contain("123");

        completed.Result.TryGetProperty("content", out var content).Should().BeTrue();
        content.ValueKind.Should().Be(JsonValueKind.Array);
        content.GetRawText().Should().Contain("hello hi");
    }

    private sealed class FixedMcpInvoker : IMcpToolInvoker
    {
        private readonly string _name;
        private readonly JsonElement _result;

        public FixedMcpInvoker(string name, JsonElement result)
        {
            _name = name;
            _result = result;
        }

        public bool CanInvoke(string toolName) => toolName == _name;

        public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    private sealed class ToolThenAnswerChatClient : MeaiIChatClient
    {
        private readonly string _toolName;
        private int _calls;

        public ToolThenAnswerChatClient(string toolName) => _toolName = toolName;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step1()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "call mcp tool" }),
                        new MeaiFunctionCallContent("call_1", _toolName, new Dictionary<string, object?> { ["message"] = "hi" })
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("done") },
                };
            }

            return _calls == 1 ? Step1() : Step2();
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class InMemorySessionStore : ISessionStore
    {
        private readonly Dictionary<string, SessionMetadata> _meta = new();
        private readonly Dictionary<string, List<SessionEvent>> _events = new();

        public void CreateNew(string sessionId, SessionMetadata metadata)
        {
            _meta[sessionId] = metadata;
            _events[sessionId] = new List<SessionEvent>();
        }

        public bool Exists(string sessionId) => _events.ContainsKey(sessionId);

        public ImmutableArray<string> ListSessionIds() => _events.Keys.OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();

        public SessionMetadata? TryLoadMetadata(string sessionId) => _meta.TryGetValue(sessionId, out var v) ? v : null;

        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) =>
            _events.TryGetValue(sessionId, out var list) ? list.ToImmutableArray() : ImmutableArray<SessionEvent>.Empty;

        public void AppendCommitted(string sessionId, SessionEvent evt)
        {
            if (!_events.TryGetValue(sessionId, out var list))
                _events[sessionId] = list = new List<SessionEvent>();
            list.Add(evt);
        }

        public void UpdateMetadata(string sessionId, SessionMetadata metadata) => _meta[sessionId] = metadata;
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new();
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException(method);
    }

    private sealed class NullSessionEvents : IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullPromptTurn : IAcpPromptTurn
    {
        public IAcpToolCalls ToolCalls { get; } = new NullToolCalls();

        private sealed class NullToolCalls : IAcpToolCalls
        {
            public IReadOnlyCollection<string> ActiveToolCallIds { get; } = Array.Empty<string>();
            public IAcpToolCall Start(string toolCallId, string title, ToolKind kind) => new NullToolCall(toolCallId);
            public Task CancelAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            private sealed class NullToolCall : IAcpToolCall
            {
                public string ToolCallId { get; }
                public NullToolCall(string toolCallId) => ToolCallId = toolCallId;
                public Task AddContentAsync(ToolCallContent content, CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task InProgressAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task CompletedAsync(CancellationToken cancellationToken = default, object? rawOutput = null) => Task.CompletedTask;
                public Task FailedAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
                public Task CancelledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
        }
    }
}
