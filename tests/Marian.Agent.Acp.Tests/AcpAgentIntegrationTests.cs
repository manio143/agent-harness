using Marian.Agent.Acp.Acp;
using Marian.Agent.Acp.Schema;

namespace Marian.Agent.Acp.Tests;

public class AcpAgentIntegrationTests
{
    [Fact]
    public async Task Initialize_Then_SessionNew_Works_EndToEnd()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var agent = new TestAgent();
        var server = new AcpAgentServer(agent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var initResp = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo
                {
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        ["name"] = "test-client",
                        ["version"] = "0.0",
                    }
                },
                ClientCapabilities = new ClientCapabilities
                {
                    Terminal = false,
                    Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
                },
            },
            cts.Token);

        Assert.Equal(1, initResp.ProtocolVersion);

        var sessionResp = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest
            {
                Cwd = "/tmp",
                McpServers = new List<McpServer>(),
            },
            cts.Token);

        Assert.Equal("ses_test", sessionResp.SessionId);

        cts.Cancel();
        try { await serverTask; } catch { /* ignore */ }
    }

    private sealed class TestAgent : IAcpAgent
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo
                {
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        ["name"] = "test-agent",
                        ["version"] = "0.0",
                    }
                },
                AgentCapabilities = new AgentCapabilities(),
                AuthMethods = new List<AuthMethod>(),
            });
        }

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new NewSessionResponse
            {
                SessionId = "ses_test",
                Modes = new Modes2(),
                ConfigOptions = null,
            });
        }

        public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
        {
            // Not exercised in this test.
            return Task.FromResult(new PromptResponse());
        }
    }
}
