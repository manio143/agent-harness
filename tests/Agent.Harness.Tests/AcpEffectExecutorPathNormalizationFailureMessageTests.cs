using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorPathNormalizationFailureMessageTests
{
    [Fact]
    public async Task WhenFsToolFails_ErrorIncludesNormalizedPath()
    {
        var store = new InMemorySessionStore();
        store.CreateNew("s1", new SessionMetadata(
            SessionId: "s1",
            Cwd: "/repo/cwd",
            Title: null,
            CreatedAtIso: "2026-01-01T00:00:00.0000000+00:00",
            UpdatedAtIso: "2026-01-01T00:00:00.0000000+00:00"));

        var client = new ThrowingFsClientCaller();
        var exec = new HarnessEffectExecutor("s1", client, new ThrowingChatClient(), store: store);

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("call_1", "write_text_file", JsonSerializer.SerializeToElement(new { path = "./a/../demo.txt", content = "x" })),
            CancellationToken.None);

        var failed = obs.OfType<ObservedToolCallFailed>().Single();
        failed.Error.Should().Contain("/repo/cwd/demo.txt");
    }

    private sealed class ThrowingFsClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("JSON-RPC error -32603: Internal error");
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

    private sealed class InMemorySessionStore : ISessionStore
    {
        private readonly Dictionary<string, SessionMetadata> _meta = new();
        private readonly Dictionary<string, List<SessionEvent>> _events = new();

        public void CreateNew(string sessionId, SessionMetadata metadata)
        {
            _meta[sessionId] = metadata;
            _events[sessionId] = new List<SessionEvent>();
        }

        public bool Exists(string sessionId) => _events.ContainsKey(sessionId);
        public ImmutableArray<string> ListSessionIds() => _events.Keys.OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        public SessionMetadata? TryLoadMetadata(string sessionId) => _meta.TryGetValue(sessionId, out var v) ? v : null;
        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) =>
            _events.TryGetValue(sessionId, out var list) ? list.ToImmutableArray() : ImmutableArray<SessionEvent>.Empty;
        public void AppendCommitted(string sessionId, SessionEvent evt)
        {
            if (!_events.TryGetValue(sessionId, out var list))
                _events[sessionId] = list = new List<SessionEvent>();
            list.Add(evt);
        }
        public void UpdateMetadata(string sessionId, SessionMetadata metadata) => _meta[sessionId] = metadata;
    }
}
