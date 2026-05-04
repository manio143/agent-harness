using System.Text.Json;
using System.Threading;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent that interleaves streaming message chunks with tool lifecycle updates.
// Goal: validate client reducer ordering + VM grouping stays stable under interleaving.

var server = new AcpAgentServer(new InterleavingFactory());

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

internal sealed class InterleavingFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-interleaving-agent",
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
        => new InterleavingSessionAgent(events);

    private sealed class InterleavingSessionAgent : IAcpSessionAgent
    {
        private readonly IAcpSessionEvents _events;

        public InterleavingSessionAgent(IAcpSessionEvents events)
        {
            _events = events;
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            // Start with intent to group tools.
            using var intentDoc = JsonDocument.Parse("{\"intent\":\"Tools\"}");
            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call",
                    toolCallId = "intent-1",
                    title = "report_intent",
                    kind = "other",
                    status = "completed",
                    rawInput = intentDoc.RootElement,
                    rawOutput = (JsonElement?)null,
                    content = Array.Empty<object>(),
                    locations = Array.Empty<object>(),
                },
                cancellationToken);

            var toolCallId = "tool-1";

            using var toolIn = JsonDocument.Parse("{\"cmd\":\"echo hi\"}");
            using var outNull = JsonDocument.Parse("null");

            // Tool appears first as pending.
            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call",
                    toolCallId,
                    title = "host.exec",
                    kind = "execute",
                    status = "pending",
                    rawInput = toolIn.RootElement,
                    rawOutput = outNull.RootElement,
                    content = Array.Empty<object>(),
                    locations = Array.Empty<object>(),
                },
                cancellationToken);

            // Interleave streaming message chunks.
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "chunk-" } },
                cancellationToken);
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "one" } },
                cancellationToken);

            // Tool update to in_progress.
            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId,
                    kind = "execute",
                    status = "in_progress",
                    rawInput = toolIn.RootElement,
                    rawOutput = outNull.RootElement,
                },
                cancellationToken);

            // More streaming.
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = " chunk-" } },
                cancellationToken);
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "two" } },
                cancellationToken);

            // Tool completes.
            using var toolOut = JsonDocument.Parse("{\"exitCode\":0,\"stdout\":\"hi\"}");
            await _events.SendSessionUpdateAsync(
                new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId,
                    kind = "execute",
                    status = "completed",
                    rawInput = toolIn.RootElement,
                    rawOutput = toolOut.RootElement,
                },
                cancellationToken);

            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = " done" } },
                cancellationToken);

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            => null;
    }
}
