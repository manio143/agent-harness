using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpToolCallContentAdditiveIntegrationTests
{
    [Fact]
    public async Task ToolCall_Content_Is_Additive_Across_Multiple_Updates()
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

        var sessionUpdates = notifications
            .Where(n => n.Method == "session/update")
            .Select(n => n.Params)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        var toolCallContentUpdates = sessionUpdates
            .Where(p => GetUpdateKind(p) == "tool_call_update")
            .Where(p => p.GetProperty("update").TryGetProperty("content", out _))
            .ToList();

        // We expect exactly two content-bearing updates ("a" then "b").
        var texts = toolCallContentUpdates.Select(ExtractFirstText).Where(t => t is not null).ToList();
        Assert.Equal(new[] { "a", "b" }, texts);

        // Also validate each update carried only the newly produced item.
        foreach (var u in toolCallContentUpdates)
        {
            var content = u.GetProperty("update").GetProperty("content");
            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            Assert.Equal(1, content.GetArrayLength());
        }

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private static string? GetUpdateKind(JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var u)) return null;
        if (!u.TryGetProperty("sessionUpdate", out var k)) return null;
        return k.GetString();
    }

    private static string? ExtractFirstText(JsonElement @params)
    {
        var update = @params.GetProperty("update");
        if (!update.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array || contentArray.GetArrayLength() == 0)
            return null;

        var first = contentArray[0];
        if (!first.TryGetProperty("type", out var t) || t.GetString() != "content") return null;
        if (!first.TryGetProperty("content", out var cb) || cb.ValueKind != JsonValueKind.Object) return null;
        if (!cb.TryGetProperty("type", out var cbt) || cbt.GetString() != "text") return null;
        if (!cb.TryGetProperty("text", out var txt)) return null;
        return txt.GetString();
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
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent();

        private sealed class Agent : IAcpSessionAgent
        {
            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                var call = turn.ToolCalls.Start("call_1", "Test", ToolKind.Other);

                await call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "a" } }, cancellationToken);
                await call.AddContentAsync(new ToolCallContentContent { Content = new TextContent { Text = "b" } }, cancellationToken);

                await call.CompletedAsync(cancellationToken);
                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
