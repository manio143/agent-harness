using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// Minimal ACP agent process intended for manual smoke-testing with `acpx --agent`.
// Transport: newline-delimited JSON over stdin/stdout.

var server = new AcpAgentServer(new MinimalFactory());

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

internal sealed class MinimalFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        // Strict initialize validation is done by the server; we just return the required fields.
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-minimal-agent",
                    ["version"] = "0.0.1",
                },
            },
            AgentCapabilities = new AgentCapabilities
            {
                PromptCapabilities = new PromptCapabilities(),
                SessionCapabilities = new SessionCapabilities
                {
                    List = new List(),
                },
                LoadSession = false,
            },
            AuthMethods = new List<AuthMethod>(),
        });
    }

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new NewSessionResponse
        {
            SessionId = Guid.NewGuid().ToString(),
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>
            {
                new SessionConfigOption
                {
                    Id = "mode",
                    Name = "Mode",
                    Type = SessionConfigOptionType.Select,
                    CurrentValue = "ask",
                    Options = new SessionConfigSelectOptions
                    {
                        new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                        new SessionConfigSelectOption { Value = "code", Name = "Code" },
                    },
                },
            },
        });
    }

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        // Demo: no persistence, just return empty.
        return Task.FromResult(new ListSessionsResponse { Sessions = new List<SessionInfo>() });
    }

    public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        => new MinimalSessionAgent(sessionId, events);

    private sealed class MinimalSessionAgent : IAcpSessionAgent
    {
        private readonly string _sessionId;
        private readonly IAcpSessionEvents _events;

        public MinimalSessionAgent(string sessionId, IAcpSessionEvents events)
        {
            _sessionId = sessionId;
            _events = events;
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            // Streaming contract: send a couple updates.
            await _events.SendSessionUpdateAsync(new AgentMessageChunk { Content = new TextContent { Text = "hello from minimal agent" } }, cancellationToken);

            // Tool call demo: start, progress, complete.
            var call = turn.ToolCalls.Start("call_1", "Demo", ToolKind.Other);
            await call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "doing work" } }, cancellationToken);
            await call.InProgressAsync(cancellationToken);
            await call.CompletedAsync(cancellationToken);

            await _events.SendSessionUpdateAsync(new AgentMessageChunk { Content = new TextContent { Text = "done" } }, cancellationToken);

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
        {
            // Echo back full config state with the new mode.
            return Task.FromResult(new SetSessionConfigOptionResponse
            {
                ConfigOptions = new List<SessionConfigOption>
                {
                    new SessionConfigOption
                    {
                        Id = request.ConfigId,
                        Name = "Mode",
                        Type = SessionConfigOptionType.Select,
                        CurrentValue = request.Value,
                        Options = new SessionConfigSelectOptions
                        {
                            new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                            new SessionConfigSelectOption { Value = "code", Name = "Code" },
                        },
                    },
                },
            });
        }
    }
}
