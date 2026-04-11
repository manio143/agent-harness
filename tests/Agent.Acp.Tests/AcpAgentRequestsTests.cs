using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpAgentRequestsTests
{
    [Fact]
    public async Task Agent_Can_Request_Client_ReadTextFile_And_Get_Response()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var agent = new FileReadingAgent();
        var server = new AcpAgentServer(agent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);
        var allowRespond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.RequestHandler = async (req, _) =>
        {
            if (req.Method != "client/readTextFile")
                throw new InvalidOperationException($"Unexpected request: {req.Method}");

            // Ensure the agent really waits for the client response (ping-pong).
            await allowRespond.Task;

            var p = req.Params!.Value;
            var sessionId = p.GetProperty("sessionId").GetString();

            var respObj = new ReadTextFileResponse { Content = $"hello from {sessionId}" };
            var respJson = JsonSerializer.Serialize(respObj, AcpJson.Options);
            using var doc = JsonDocument.Parse(respJson);
            return doc.RootElement.Clone();
        };

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = false } },
            },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var promptTask = client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        // Assert it doesn't finish before we answer the agent->client request.
        var early = await Task.WhenAny(promptTask, Task.Delay(150, cts.Token));
        Assert.NotSame(promptTask, early);

        // Now let the client respond.
        allowRespond.SetResult();

        var promptResp = await promptTask;

        // Agent should have embedded proof in meta.
        Assert.True(promptResp.AdditionalProperties.TryGetValue("readTextFileContent", out var v));
        Assert.Equal($"hello from {newSes.SessionId}", v?.ToString());

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class FileReadingAgent : IAcpAgentWithContext
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities(),
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new PromptResponse());

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpAgentContext context, CancellationToken cancellationToken)
        {
            var resp = await context.RequestAsync<ReadTextFileRequest, ReadTextFileResponse>(
                "client/readTextFile",
                new ReadTextFileRequest { SessionId = request.SessionId, Path = "/tmp/demo.txt" },
                cancellationToken);

            return new PromptResponse
            {
                StopReason = new StopReason(),
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["readTextFileContent"] = resp.Content,
                },
            };
        }
    }
}
