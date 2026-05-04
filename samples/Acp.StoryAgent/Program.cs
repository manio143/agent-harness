using System.Text.Json;
using System.Threading;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent that plays a multi-turn scripted story to validate client state transitions
// and strict timeline ordering (user -> tools -> messages -> tools).

var server = new AcpAgentServer(new StoryFactory());

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

internal sealed class StoryFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-story-agent",
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
        => new StorySessionAgent(events);

    private sealed class StorySessionAgent : IAcpSessionAgent
    {
        private readonly IAcpSessionEvents _events;
        private int _turn;

        public StorySessionAgent(IAcpSessionEvents events)
        {
            _events = events;
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            _turn++;

            if (_turn == 1)
            {
                // Intent label for tool grouping.
                using var intentDoc = JsonDocument.Parse("{\"intent\":\"Search\"}");
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

                // Tool call 1
                using var in1 = JsonDocument.Parse("{\"q\":\"cats\"}");
                using var out1 = JsonDocument.Parse("{\"hits\":1}");
                await _events.SendSessionUpdateAsync(
                    new
                    {
                        sessionUpdate = "tool_call",
                        toolCallId = "tool-1",
                        title = "web.search",
                        kind = "search",
                        status = "completed",
                        rawInput = in1.RootElement,
                        rawOutput = out1.RootElement,
                        content = Array.Empty<object>(),
                        locations = Array.Empty<object>(),
                    },
                    cancellationToken);

                // Message
                await _events.SendSessionUpdateAsync(
                    new AgentMessageChunk { Content = new TextContent { Text = "assistant: after first tool" } },
                    cancellationToken);

                // Non-contiguous tool call of the same intent => must become a NEW UI group.
                using var in2 = JsonDocument.Parse("{\"url\":\"https://example.com\"}");
                using var out2 = JsonDocument.Parse("{\"status\":200}");
                await _events.SendSessionUpdateAsync(
                    new
                    {
                        sessionUpdate = "tool_call",
                        toolCallId = "tool-2",
                        title = "http.fetch",
                        kind = "fetch",
                        status = "completed",
                        rawInput = in2.RootElement,
                        rawOutput = out2.RootElement,
                        content = Array.Empty<object>(),
                        locations = Array.Empty<object>(),
                    },
                    cancellationToken);

                await _events.SendSessionUpdateAsync(
                    new AgentMessageChunk { Content = new TextContent { Text = "assistant: done turn1" } },
                    cancellationToken);

                return new PromptResponse { StopReason = StopReason.EndTurn };
            }

            if (_turn == 2)
            {
                await _events.SendSessionUpdateAsync(
                    new AgentThoughtChunk { Content = new TextContent { Text = "reason-" } },
                    cancellationToken);
                await _events.SendSessionUpdateAsync(
                    new AgentThoughtChunk { Content = new TextContent { Text = "ing" } },
                    cancellationToken);
                await _events.SendSessionUpdateAsync(
                    new AgentMessageChunk { Content = new TextContent { Text = "assistant: done turn2" } },
                    cancellationToken);
                return new PromptResponse { StopReason = StopReason.EndTurn };
            }

            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "assistant: extra" } },
                cancellationToken);
            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            => null;
    }
}
