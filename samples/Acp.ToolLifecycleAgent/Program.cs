using System.Text.Json;
using System.Threading;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent focused on deterministic tool call lifecycle updates.

var server = new AcpAgentServer(new ToolLifecycleFactory());

await using var transport = new LineDelimitedStreamTransport(
    input: Console.OpenStandardInput(),
    output: Console.OpenStandardOutput(),
    name: "stdio");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await server.RunAsync(transport, cts.Token);

internal sealed class ToolLifecycleFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-tool-lifecycle-agent",
                    ["version"] = "0.0.1",
                },
            },
            AgentCapabilities = new AgentCapabilities
            {
                PromptCapabilities = new PromptCapabilities(),
                SessionCapabilities = new SessionCapabilities(),
                LoadSession = false,
            },
            AuthMethods = new List<AuthMethod>(),
        });

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new NewSessionResponse
        {
            SessionId = Guid.NewGuid().ToString(),
            Modes = null,
            ConfigOptions = null,
        });

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken) => null;

    public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken) => null;

    public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        => new ToolLifecycleSessionAgent(events);

    private sealed class ToolLifecycleSessionAgent : IAcpSessionAgent
    {
        private readonly IAcpSessionEvents _events;

        public ToolLifecycleSessionAgent(IAcpSessionEvents events)
        {
            _events = events;
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            var toolCallId = "tool-001";

            using var inputDoc = JsonDocument.Parse("{\"cmd\":\"ls\"}");
            using var outputNullDoc = JsonDocument.Parse("null");

            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call",
                    toolCallId,
                    title = "host.exec",
                    kind = "execute",
                    status = "pending",
                    rawInput = inputDoc.RootElement,
                    rawOutput = outputNullDoc.RootElement,
                    content = Array.Empty<object>(),
                    locations = Array.Empty<object>(),
                },
                cancellationToken);

            // In progress
            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId,
                    kind = "execute",
                    status = "in_progress",
                    rawInput = inputDoc.RootElement,
                    rawOutput = outputNullDoc.RootElement,
                },
                cancellationToken);

            // Completed with output
            using var outputDoc = JsonDocument.Parse("{\"exitCode\":0,\"stdout\":\"ok\"}");

            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId,
                    kind = "execute",
                    status = "completed",
                    rawInput = inputDoc.RootElement,
                    rawOutput = outputDoc.RootElement,
                },
                cancellationToken);

            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "done" } },
                cancellationToken);

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            => null;
    }
}
