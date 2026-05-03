using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

/// <summary>
/// Validates that ACP enum types round-trip through <see cref="AcpJson.Options"/> as lowercase
/// strings (e.g. "select", not "Select") so that strict ACP clients such as Zed can deserialize
/// session/new and permission-request responses without error.
///
/// Root cause: NJsonSchema emits per-property [JsonConverter(typeof(JsonStringEnumConverter))]
/// without a naming policy, which overrides the global JsonStringEnumConverter(CamelCase) in
/// AcpJson.Options and produces PascalCase values.
/// </summary>
public class AcpEnumCasingTests
{
    // ── SessionConfigOptionType ───────────────────────────────────────────────

    [Fact]
    public void SessionConfigOptionType_Select_SerializesAs_lowercase_select()
    {
        var json = JsonSerializer.Serialize(SessionConfigOptionType.Select, AcpJson.Options);
        Assert.Equal("\"select\"", json);
    }

    [Fact]
    public void SessionConfigOptionType_Select_DeserializesFrom_lowercase_select()
    {
        var value = JsonSerializer.Deserialize<SessionConfigOptionType>("\"select\"", AcpJson.Options);
        Assert.Equal(SessionConfigOptionType.Select, value);
    }

    [Fact]
    public void SessionConfigOption_Type_Field_SerializesAs_lowercase_select()
    {
        var option = new SessionConfigOption
        {
            Id = "mode",
            Name = "Mode",
            Type = SessionConfigOptionType.Select,
            CurrentValue = "ask",
            Options = new SessionConfigSelectOptions
            {
                new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
            },
        };

        var json = JsonSerializer.Serialize(option, AcpJson.Options);
        using var doc = JsonDocument.Parse(json);
        var typeValue = doc.RootElement.GetProperty("type").GetString();

        Assert.Equal("select", typeValue);
    }

    // ── RequestPermissionOutcomeOutcome ───────────────────────────────────────

    [Theory]
    [InlineData(RequestPermissionOutcomeOutcome.Cancelled, "cancelled")]
    [InlineData(RequestPermissionOutcomeOutcome.Selected, "selected")]
    public void RequestPermissionOutcomeOutcome_SerializesAs_lowercase(
        RequestPermissionOutcomeOutcome value, string expected)
    {
        var json = JsonSerializer.Serialize(value, AcpJson.Options);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Theory]
    [InlineData("cancelled", RequestPermissionOutcomeOutcome.Cancelled)]
    [InlineData("selected", RequestPermissionOutcomeOutcome.Selected)]
    public void RequestPermissionOutcomeOutcome_DeserializesFrom_lowercase(
        string json, RequestPermissionOutcomeOutcome expected)
    {
        var value = JsonSerializer.Deserialize<RequestPermissionOutcomeOutcome>($"\"{json}\"", AcpJson.Options);
        Assert.Equal(expected, value);
    }

    // ── Wire-level integration: session/new response ──────────────────────────

    [Fact]
    public async Task SessionNew_WireResponse_ContainsLowercase_type_in_configOptions()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        // Capture raw JSON-RPC messages sent by the server so we can inspect the wire bytes.
        var messages = new System.Collections.Concurrent.ConcurrentQueue<JsonRpcMessage>();
        var capture = new CapturingTransport(serverTransport, messages);

        var server = new AcpAgentServer(new SessionNewFactory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(capture, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        _ = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        cts.Cancel();
        try { await serverTask; } catch { }

        // Find the session/new JSON-RPC response: it contains "sessionId" in the result.
        var response = messages
            .OfType<JsonRpcResponse>()
            .FirstOrDefault(r =>
            {
                var raw = r.Result.GetRawText();
                using var d = JsonDocument.Parse(raw);
                return d.RootElement.TryGetProperty("sessionId", out _);
            });
        Assert.NotNull(response);

        // Re-serialize the result element to verify wire casing.
        var resultJson = response.Result.GetRawText();
        using var doc = JsonDocument.Parse(resultJson);

        var configOptions = doc.RootElement.GetProperty("configOptions");
        Assert.True(configOptions.GetArrayLength() > 0, "Expected at least one configOption");

        var typeValue = configOptions[0].GetProperty("type").GetString();
        Assert.Equal("select", typeValue);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class SessionNewFactory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities() },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse
            {
                SessionId = "ses_casing_test",
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
                        },
                    },
                },
            });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new NoopSessionAgent();

        private sealed class NoopSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }

    /// <summary>
    /// Wraps a transport and records every outbound (server→client) message.
    /// </summary>
    private sealed class CapturingTransport : Agent.Acp.Transport.ITransport
    {
        private readonly Agent.Acp.Transport.ITransport _inner;
        private readonly System.Collections.Concurrent.ConcurrentQueue<JsonRpcMessage> _sent;

        public CapturingTransport(Agent.Acp.Transport.ITransport inner, System.Collections.Concurrent.ConcurrentQueue<JsonRpcMessage> sent)
        {
            _inner = inner;
            _sent = sent;
        }

        public string Name => _inner.Name + "+capture";
        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => _inner.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            _sent.Enqueue(message);
            return _inner.SendMessageAsync(message, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
