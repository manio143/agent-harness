using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpInitializeMetaJsonContractTests
{
    [Fact]
    public async Task MetaJson_MethodNames_Are_Respected_By_The_Server()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var metaPath = Path.Combine(repoRoot, "schema", "meta.json");
        Assert.True(File.Exists(metaPath), $"Expected meta.json at: {metaPath}");

        using var metaDoc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var root = metaDoc.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt32());

        var agentMethods = root.GetProperty("agentMethods");
        var initializeMethod = agentMethods.GetProperty("initialize").GetString();
        Assert.Equal("initialize", initializeMethod);

        // Smoke the actual wire method name (ensures we don't drift from meta.json).
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var resp = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            initializeMethod!,
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo
                {
                    AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" },
                },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        Assert.Equal(1, resp.ProtocolVersion);

        cts.Cancel();
        try { await serverTask; } catch { }
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
            new NoopSessionAgent();

        private sealed class NoopSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }

    private static string FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Agent.slnx");
            if (File.Exists(candidate))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx not found)." );
    }
}
