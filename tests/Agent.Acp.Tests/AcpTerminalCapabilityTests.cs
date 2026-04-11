using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpTerminalCapabilityTests
{
    [Fact]
    public async Task Terminal_Methods_Throw_When_Client_DidNotAdvertise_Capability()
    {
        var client = new FakeClientCaller(new ClientCapabilities
        {
            Terminal = false,
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateTerminalAsync(new CreateTerminalRequest
        {
            SessionId = "ses",
            Command = "echo",
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTerminalOutputAsync(new TerminalOutputRequest
        {
            SessionId = "ses",
            TerminalId = "t1",
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WaitForTerminalExitAsync(new WaitForTerminalExitRequest
        {
            SessionId = "ses",
            TerminalId = "t1",
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.KillTerminalAsync(new KillTerminalRequest
        {
            SessionId = "ses",
            TerminalId = "t1",
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReleaseTerminalAsync(new ReleaseTerminalRequest
        {
            SessionId = "ses",
            TerminalId = "t1",
        }));
    }

    private sealed class FakeClientCaller : IAcpClientCallerWithCapabilities
    {
        public FakeClientCaller(ClientCapabilities caps)
        {
            ClientCapabilities = caps;
        }

        public ClientCapabilities ClientCapabilities { get; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Should not reach transport when capability is missing");
        }
    }
}
