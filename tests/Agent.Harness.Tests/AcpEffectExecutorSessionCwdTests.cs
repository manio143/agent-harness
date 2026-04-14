using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpEffectExecutorSessionCwdTests
{
    [Fact]
    public async Task WhenSessionCwdProvided_DoesNotReadMetadataForFsNormalization()
    {
        var store = new ThrowingMetadataStore();
        var client = new CapturingClientCaller();
        var exec = new AcpEffectExecutor(
            sessionId: "s1",
            client: client,
            chat: new ThrowingChatClient(),
            sessionCwd: "/repo/cwd",
            store: store);

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("call_1", "write_text_file", JsonSerializer.SerializeToElement(new { path = "./demo.txt", content = "hello" })),
            CancellationToken.None);

        obs.Should().ContainSingle(e => e is ObservedToolCallCompleted);
        client.LastWrite!.Path.Should().Be("/repo/cwd/demo.txt");
    }

    private sealed class ThrowingMetadataStore : ISessionStore
    {
        public void CreateNew(string sessionId, SessionMetadata metadata) => throw new NotImplementedException();
        public bool Exists(string sessionId) => true;
        public ImmutableArray<string> ListSessionIds() => ImmutableArray<string>.Empty;
        public SessionMetadata? TryLoadMetadata(string sessionId) => throw new Exception("metadata should not be read");
        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) => ImmutableArray<SessionEvent>.Empty;
        public void AppendCommitted(string sessionId, SessionEvent evt) => throw new NotImplementedException();
        public void UpdateMetadata(string sessionId, SessionMetadata metadata) => throw new NotImplementedException();
    }

    private sealed class CapturingClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        };

        public WriteTextFileRequest? LastWrite { get; private set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            if (method == "fs/write_text_file")
            {
                LastWrite = request as WriteTextFileRequest;
                return Task.FromResult((TResponse)(object?)null!);
            }

            throw new NotImplementedException(method);
        }
    }

    private sealed class ThrowingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
