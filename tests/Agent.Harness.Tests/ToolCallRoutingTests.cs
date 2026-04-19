using System.Collections.Immutable;
using System.Text.Json;
using System.Linq;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Tools.Executors;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class ToolCallRoutingTests
{
    [Fact]
    public async Task ExecuteToolCall_thread_list_IsHandledBySystemExecutor()
    {
        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient());

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ThreadList),
        };

        var observed = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall("call_1", "thread_list", new { }),
            CancellationToken.None);

        observed.Should().ContainSingle(o => o is ObservedToolCallCompleted);
    }

    [Fact]
    public async Task ExecuteToolCall_McpTool_IsRoutedToMcpInvoker()
    {
        var mcp = new CapturingMcpInvoker();

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient(),
            mcp: mcp);

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(new ToolDefinition(
                Name: "mcp_tool",
                Description: "",
                InputSchema: JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())),
        };

        var observed = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall("call_1", "mcp_tool", new { x = 1 }),
            CancellationToken.None);

        mcp.Invoked.Should().BeTrue();
        observed.Should().ContainSingle(o => o is ObservedToolCallCompleted);
    }

    private sealed class CapturingMcpInvoker : IMcpToolInvoker
    {
        public bool Invoked { get; private set; }

        public bool CanInvoke(string toolName) => toolName == "mcp_tool";

        public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        {
            Invoked = true;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        }
    }

    private sealed class NullMeaiChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NullAcpClientCaller(ClientCapabilities caps) : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unsupported method: {method}");
    }
}
