using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class AcpEffectExecutorPathNormalizationTests
{
    [Fact]
    public async Task WriteTextFile_WhenPathIsRelative_NormalizesToAbsoluteUsingSessionCwd()
    {
        var store = new InMemorySessionStore();
        store.CreateNew("s1", new SessionMetadata(
            SessionId: "s1",
            Cwd: "/repo/cwd",
            Title: null,
            CreatedAtIso: "2026-01-01T00:00:00.0000000+00:00",
            UpdatedAtIso: "2026-01-01T00:00:00.0000000+00:00"));

        var client = new CapturingClientCaller();
        var chat = new ThrowingChatClient();
        var exec = new AcpEffectExecutor("s1", client, chat, store: store);

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("call_1", "write_text_file", JsonSerializer.SerializeToElement(new { path = "./a/../demo.txt", content = "hello" })),
            CancellationToken.None);

        obs.Should().ContainSingle(e => e is ObservedToolCallCompleted);

        client.LastWrite!.Path.Should().Be("/repo/cwd/demo.txt");
    }

    [Fact]
    public async Task ReadTextFile_WhenPathIsAbsolute_PassesThrough()
    {
        var store = new InMemorySessionStore();
        store.CreateNew("s1", new SessionMetadata(
            SessionId: "s1",
            Cwd: "/repo/cwd",
            Title: null,
            CreatedAtIso: "2026-01-01T00:00:00.0000000+00:00",
            UpdatedAtIso: "2026-01-01T00:00:00.0000000+00:00"));

        var client = new CapturingClientCaller { ReadResponse = new ReadTextFileResponse { Content = "x" } };
        var chat = new ThrowingChatClient();
        var exec = new AcpEffectExecutor("s1", client, chat, store: store);

        var abs = "/repo/cwd/demo.txt";

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("call_1", "read_text_file", JsonSerializer.SerializeToElement(new { path = abs })),
            CancellationToken.None);

        obs.Should().ContainSingle(e => e is ObservedToolCallCompleted);
        client.LastRead!.Path.Should().Be(abs);
    }

    private sealed class CapturingClientCaller : IAcpClientCaller
    {
        public ReadTextFileResponse? ReadResponse { get; set; }
        public ReadTextFileRequest? LastRead { get; private set; }
        public WriteTextFileRequest? LastWrite { get; private set; }

        public ClientCapabilities ClientCapabilities { get; } = new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            switch (method)
            {
                case "fs/read_text_file":
                    LastRead = request as ReadTextFileRequest;
                    return Task.FromResult((TResponse)(object)(ReadResponse ?? new ReadTextFileResponse { Content = "" }));

                case "fs/write_text_file":
                    LastWrite = request as WriteTextFileRequest;
                    return Task.FromResult((TResponse)(object?)null!);

                default:
                    throw new NotImplementedException(method);
            }
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
