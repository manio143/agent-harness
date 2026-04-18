using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorObservePersistsViaSinkTests
{
    private sealed class RecordingThreadStore : IThreadStore
    {
        public List<(string SessionId, string ThreadId, SessionEvent Event)> Appends { get; } = new();

        public void CreateMainIfMissing(string sessionId) { }
        public ThreadMetadata? TryLoadThreadMetadata(string sessionId, string threadId) => null;
        public ImmutableArray<ThreadMetadata> ListThreads(string sessionId) => ImmutableArray<ThreadMetadata>.Empty;
        public void CreateThread(string sessionId, ThreadMetadata metadata) { }
        public void SaveThreadMetadata(string sessionId, ThreadMetadata metadata) { }

        public void AppendCommittedEvent(string sessionId, string threadId, SessionEvent evt)
            => Appends.Add((sessionId, threadId, evt));

        public ImmutableArray<SessionEvent> LoadCommittedEvents(string sessionId, string threadId)
            => ImmutableArray<SessionEvent>.Empty;
    }

    private sealed class NullChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "ignored")
            }));

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

    [Fact]
    public async Task ObserveAsync_does_not_write_directly_to_thread_store()
    {
        // This test encodes the desired invariant:
        // orchestrator logic must not call IThreadStore.AppendCommittedEvent directly.
        // Instead, commits should flow through IEventSink.
        //
        // Current implementation does write directly, so this test SHOULD FAIL until refactor.

        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new RecordingThreadStore();

        var sessionId = "sess1";
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:01Z"));

        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: new NullChatClient(),
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: new ThreadManager(sessionId, threadStore));

        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("Hi"));

        threadStore.Appends.Should().BeEmpty("ObserveAsync must not append committed events directly; commits should flow via sinks");
    }
}
