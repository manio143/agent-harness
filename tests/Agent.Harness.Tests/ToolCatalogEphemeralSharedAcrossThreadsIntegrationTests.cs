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
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class ToolCatalogEphemeralSharedAcrossThreadsIntegrationTests
{
    [Fact]
    public async Task McpToolInCatalog_IsRunnableFromChildThread_WhenCatalogRefreshedPerPrompt()
    {
        var sessionId = "ses_tool_catalog_shared";
        var root = Path.Combine(Path.GetTempPath(), "harness-tool-catalog-shared", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        // Pre-existing tool (MCP) that should be runnable from *any* thread.
        var mcpTool = new ToolDefinition(
            Name: "mcp_echo",
            Description: "Echo tool (MCP)",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "text": { "type": "string" }
              },
              "required": ["text"]
            }
            """).RootElement.Clone());

        var chat = new ScriptedChat();
        var mcp = new EchoMcpToolInvoker();

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: new NullSessionEvents(),
            coreOptions: new CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new AcpPublishOptions(PublishReasoning: false),
            store: store,
            // Seed with the MCP tool so the catalog merge includes it.
            initialState: SessionState.Empty with { Tools = ImmutableArray.Create(mcpTool) },
            mcp: mcp);

        await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Hi" } },
            },
            new NullPromptTurn(),
            CancellationToken.None);

        mcp.InvokeCount.Should().Be(1);
    }

    private sealed class EchoMcpToolInvoker : IMcpToolInvoker
    {
        public int InvokeCount { get; private set; }

        public bool CanInvoke(string toolName) => toolName == "mcp_echo";

        public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        {
            InvokeCount++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        }
    }

    private sealed class ScriptedChat : MeaiIChatClient
    {
        private int _mainCalls;
        private int _childCalls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = string.Join("\n", messages.Select(m => m.Text ?? ""));
            var isChild = text.Contains("Initial message", StringComparison.Ordinal) || text.Contains("mcp", StringComparison.OrdinalIgnoreCase);

            if (isChild)
            {
                _childCalls++;
                return _childCalls switch
                {
                    1 => Child_CallMcpTool(),
                    _ => Child_Text(),
                };
            }

            _mainCalls++;
            return _mainCalls switch
            {
                1 => Main_SpawnChild(),
                _ => Main_Text(),
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Main_SpawnChild()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("call_m0", "report_intent", new Dictionary<string, object?> { ["intent"] = "spawn child" }),
                    new MeaiFunctionCallContent("call_m1", "thread_start", new Dictionary<string, object?> { ["name"] = "child", ["context"] = "fork", ["message"] = "Initial message: child should call mcp_echo", ["delivery"] = "immediate" }),
                }
            };
            await Task.CompletedTask;
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Text()
        {
            yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("main-done") } };
            await Task.CompletedTask;
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Child_CallMcpTool()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("call_c0", "report_intent", new Dictionary<string, object?> { ["intent"] = "use mcp" }),
                    new MeaiFunctionCallContent("call_c1", "mcp_echo", new Dictionary<string, object?> { ["text"] = "hello" }),
                }
            };
            await Task.CompletedTask;
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text()
        {
            yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("child-done") } };
            await Task.CompletedTask;
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException(method);
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
