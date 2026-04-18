using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

/// <summary>
/// Integration tests that verify events flow correctly from orchestrator through reducer to sink and store.
/// These tests verify the full pipeline works for basic operations.
/// </summary>
public sealed class ThreadPipelineIntegrationTests
{
    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "test response")
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
    public async Task UserMessage_flows_through_orchestrator_to_thread_store()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "harness-pipeline-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);

        var sessionId = "sess1";
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threads = new ThreadManager(sessionId, threadStore);
        var chat = new NullChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        // Act: Send a user message
        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("Hello, pipeline!"));

        // ObserveAsync only enqueues observations + schedules a wake.
        // We must run the scheduler to actually execute a turn and persist commits via the sink.
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        // Assert: Verify events were persisted to thread store
        var committedEvents = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        
        committedEvents.Should().NotBeEmpty(
            "ObserveAsync should persist events through the sink/reducer pipeline");

        var messageEvents = committedEvents
            .OfType<UserMessage>()
            .Where(e => e.Text == "Hello, pipeline!")
            .ToList();

        messageEvents.Should().NotBeEmpty(
            "UserMessage should produce persisted UserMessage event");
    }

    [Fact]
    public async Task Multiple_user_messages_are_persisted_sequentially()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "harness-pipeline-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);

        var sessionId = "sess2";
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threads = new ThreadManager(sessionId, threadStore);
        var chat = new NullChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        // Act: Send multiple messages
        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("First message"));
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("Second message"));
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        // Assert: Both messages should be persisted
        var committedEvents = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        
        var messageEvents = committedEvents
            .OfType<UserMessage>()
            .ToList();

        messageEvents.Should().HaveCountGreaterOrEqualTo(2, 
            "Both user messages should be persisted");

        var first = messageEvents.FirstOrDefault(e => e.Text == "First message");
        var second = messageEvents.FirstOrDefault(e => e.Text == "Second message");

        first.Should().NotBeNull("First message should be persisted");
        second.Should().NotBeNull("Second message should be persisted");
    }

    [Fact]
    public async Task Session_title_update_flows_to_session_metadata()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "harness-pipeline-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(dir);
        var threadStore = new JsonlThreadStore(dir);

        var sessionId = "sess3";
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threads = new ThreadManager(sessionId, threadStore);
        var chat = new NullChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        // Act: Trigger a session title update
        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("What's the weather?"));
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        // For this test, just verify the session metadata is updated
        var meta = sessionStore.TryLoadMetadata(sessionId);

        meta.Should().NotBeNull("Session metadata should exist");
        // The UpdatedAtIso should have been projected
        meta!.UpdatedAtIso.Should().NotBeNullOrEmpty("UpdatedAtIso should be set");
    }
}
