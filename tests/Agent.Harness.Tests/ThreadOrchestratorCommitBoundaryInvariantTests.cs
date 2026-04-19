using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorCommitBoundaryInvariantTests
{
    [Fact]
    public async Task ChildIdleNotification_IsCommittedInsideParentTurn_NoEventsBetweenTurnEndAndTurnStart()
    {
        var sessionId = "s1";
        var threadStore = new InMemoryThreadStore();
        threadStore.CreateMainIfMissing(sessionId);

        var threads = new ThreadManager(sessionId, threadStore);

        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new StubAcpClientCaller(new ClientCapabilities()),
            chat: new NoopChatClient(),
            chatByModel: _ => new NoopChatClient(),
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: new InMemorySessionStore(),
            threadStore: threadStore,
            threadAppender: threadStore,
            threads: threads);

        // Create a child thread that has a parent.
        var childId = "thr_child";
        var now = DateTimeOffset.UtcNow.ToString("O");
        threadStore.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: childId,
            ParentThreadId: ThreadIds.Main,
            Intent: "demo",
            CreatedAtIso: now,
            UpdatedAtIso: now,
            Model: "default"));

        // Run child once (it will stabilize/end and then notify parent).
        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);
        orchestrator.ScheduleRun(childId);
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        var parent = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);

        // Invariant: no inbox enqueue committed outside a parent turn.
        // Concretely: any ThreadInboxMessageEnqueued must be preceded by TurnStarted since last TurnEnded.
        var lastTurnEnded = -1;
        for (var i = 0; i < parent.Length; i++)
        {
            if (parent[i] is TurnEnded) lastTurnEnded = i;

            if (parent[i] is ThreadInboxMessageEnqueued)
            {
                parent.Skip(lastTurnEnded + 1)
                    .Take(i - (lastTurnEnded + 1))
                    .Should().Contain(e => e is TurnStarted);
            }
        }
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

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return YieldNone();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldNone()
            {
                await Task.CompletedTask;
                yield break;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StubAcpClientCaller : IAcpClientCaller
    {
        public StubAcpClientCaller(ClientCapabilities caps) => ClientCapabilities = caps;

        public ClientCapabilities ClientCapabilities { get; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Client caller not used in this test.");
    }
}
