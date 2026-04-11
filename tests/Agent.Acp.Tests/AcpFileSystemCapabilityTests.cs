using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpFileSystemCapabilityTests
{
    [Fact]
    public async Task ReadTextFileAsync_Throws_When_Client_DidNotAdvertise_Capability()
    {
        var client = new FakeClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = true } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadTextFileAsync(new ReadTextFileRequest
        {
            SessionId = "ses",
            Path = "/tmp/a.txt",
        }));
    }

    [Fact]
    public async Task WriteTextFileAsync_Throws_When_Client_DidNotAdvertise_Capability()
    {
        var client = new FakeClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = false } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WriteTextFileAsync(new WriteTextFileRequest
        {
            SessionId = "ses",
            Path = "/tmp/a.txt",
            Content = "hi",
        }));
    }

    private sealed class FakeClientCaller : IAcpClientCaller
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
