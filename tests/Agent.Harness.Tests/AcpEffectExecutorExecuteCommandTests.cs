using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class AcpEffectExecutorExecuteCommandTests
{
    [Fact]
    public async Task ExecuteToolCall_execute_command_UsesCommandAndArgsFields_AndCallsTerminalCreate()
    {
        var caps = new ClientCapabilities { Terminal = true };
        var client = new CapturingTerminalClientCaller(caps)
        {
            OutputResponse = new TerminalOutputResponse
            {
                ExitStatus = new ExitStatus(),
                Output = "hello",
                Truncated = false,
            },
        };

        var exec = new AcpEffectExecutor(
            sessionId: "sess1",
            client: client,
            chat: new NullMeaiChatClient());

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ExecuteCommand),
        };

        var observed = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall(
                ToolId: "call_1",
                ToolName: "execute_command",
                Args: new { command = "uname", args = new[] { "-a" } }),
            CancellationToken.None);

        client.CreateRequests.Should().HaveCount(1);
        client.CreateRequests[0].Command.Should().Be("uname");
        client.CreateRequests[0].Args.Should().Equal(new[] { "-a" });

        observed.Should().ContainSingle(e => e is ObservedToolCallCompleted);
    }

    private sealed class CapturingTerminalClientCaller(ClientCapabilities caps) : IAcpClientCaller
    {
        public List<CreateTerminalRequest> CreateRequests { get; } = new();

        public TerminalOutputResponse OutputResponse { get; init; } = new()
        {
            ExitStatus = new ExitStatus(),
            Output = "",
            Truncated = false,
        };

        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            object resp = method switch
            {
                "terminal/create" => HandleCreate((CreateTerminalRequest)(object)request!),
                "terminal/wait_for_exit" => new WaitForTerminalExitResponse { ExitCode = 0 },
                "terminal/output" => OutputResponse,
                _ => throw new NotSupportedException($"Unsupported method: {method}"),
            };

            return Task.FromResult((TResponse)resp);
        }

        private CreateTerminalResponse HandleCreate(CreateTerminalRequest req)
        {
            CreateRequests.Add(req);
            return new CreateTerminalResponse { TerminalId = "term_1" };
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
}
