using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MainThreadCapabilitiesSeedingIntegrationTests
{
    [Fact]
    public void HarnessAcpSessionAgent_WhenMainThreadCapabilitiesProvided_SeedsMainThreadMetadataIfMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "main-thread-caps", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata("s1", "/tmp", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var caps = new ThreadCapabilitiesSpec(
            Allow: ["fs.read"],
            Deny: []);

        _ = new HarnessAcpSessionAgent(
            sessionId: "s1",
            client: new NullClientCaller(),
            chat: new NullChatClient(),
            chatByModel: _ => new NullChatClient(),
            quickWorkModel: "default",
            events: new NullSessionEvents(),
            coreOptions: new CoreOptions(),
            publishOptions: new AcpPublishOptions(),
            store: store,
            initialState: SessionState.Empty,
            mainThreadCapabilities: caps);

        var threadStore = new JsonlThreadStore(root);
        var meta = threadStore.TryLoadThreadMetadata("s1", ThreadIds.Main);

        meta.Should().NotBeNull();
        meta!.Capabilities.Should().NotBeNull();
        meta.Capabilities!.Allow.Should().ContainSingle().Which.Should().Be("fs.read");
        meta.Capabilities.Deny.Should().BeEmpty();
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
            Terminal = false,
        };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Client RPC not expected in this test");
    }

    private sealed class NullSessionEvents : IAcpSessionEvents
    {
        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return YieldStop();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldStop()
            {
                await Task.CompletedTask;
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
