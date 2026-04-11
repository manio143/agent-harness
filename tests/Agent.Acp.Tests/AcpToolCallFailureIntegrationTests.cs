using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpToolCallFailureIntegrationTests
{
    [Fact]
    public async Task ToolCall_Can_Fail_And_Emits_Error_In_ToolCallUpdate()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var notifications = new List<JsonRpcNotification>();
        client.NotificationReceived += n => notifications.Add(n);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        // We expect at least:
        // - tool_call (pending)
        // - tool_call_update (content)
        // - tool_call_update (failed + rawOutput.error)
        var sessionUpdates = notifications
            .Where(n => n.Method == "session/update")
            .Select(n => n.Params)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        Assert.True(sessionUpdates.Count >= 2, "Expected at least 2 session/update notifications");

        var hasToolCall = sessionUpdates.Any(p => GetUpdateKind(p) == "tool_call");
        Assert.True(hasToolCall, "Expected a tool_call session/update");

        JsonElement? failedUpdate = sessionUpdates.FirstOrDefault(p => GetUpdateKind(p) == "tool_call_update" && GetStatus(p) == "failed");
        Assert.True(failedUpdate.HasValue, "Expected a tool_call_update with status=failed");

        var error = GetRawOutputError(failedUpdate.Value);
        Assert.Equal("boom", error);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private static string? GetUpdateKind(JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var u)) return null;
        if (!u.TryGetProperty("sessionUpdate", out var k)) return null;
        return k.GetString();
    }

    private static string? GetStatus(JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var u)) return null;
        if (!u.TryGetProperty("status", out var s)) return null;
        return s.GetString();
    }

    private static string? GetRawOutputError(JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var u)) return null;
        if (!u.TryGetProperty("rawOutput", out var ro)) return null;
        if (ro.ValueKind != JsonValueKind.Object) return null;
        if (!ro.TryGetProperty("error", out var e)) return null;
        return e.GetString();
    }

    private sealed class Factory : IAcpAgentFactory
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
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent();

        private sealed class Agent : IAcpSessionAgent
        {
            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                var call = turn.ToolCalls.Start("call_1", "Test", ToolKind.Other);
                await call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "starting" } }, cancellationToken);
                await call.InProgressAsync(cancellationToken);

                await call.FailedAsync("boom", cancellationToken);

                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
