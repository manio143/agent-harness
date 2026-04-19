using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using Agent.Harness.Tests.TestChatClients;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorSingleInFlightModelCallIntegrationTests
{
    [Fact]
    public async Task A_thread_never_has_two_model_calls_in_flight_even_if_scheduled_twice()
    {
        var sessionId = "sess_single_inflight";
        var root = Path.Combine(Path.GetTempPath(), "harness-single-inflight", Guid.NewGuid().ToString("N"));

        var sessionStore = new JsonlSessionStore(root);
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threadStore = new JsonlThreadStore(root);
        var threads = new ThreadManager(sessionId, threadStore);

        var chat = new BlockingChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions { CommitAssistantTextDeltas = false },
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threadAppender: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray.Create(ToolSchemas.ReportIntent));

        // Enqueue a prompt so the next run will call the model.
        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("hi"));

        // Schedule the same thread twice (simulates duplicate wakes).
        orchestrator.ScheduleRun(ThreadIds.Main);
        orchestrator.ScheduleRun(ThreadIds.Main);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Run orchestrator in background so we can observe the in-flight state.
        var runTask = orchestrator.RunUntilQuiescentAsync(cts.Token);

        // Wait until the first model call has started.
        await chat.FirstCallStarted.Task.WaitAsync(cts.Token);

        // Assert: no second concurrent call has started.
        chat.ConcurrentCallsMax.Should().Be(1);

        // Allow the model call to complete.
        chat.AllowCompletion.TrySetResult(true);

        await runTask;

        // Still should never have exceeded 1.
        chat.ConcurrentCallsMax.Should().Be(1);
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

}
