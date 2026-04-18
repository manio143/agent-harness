using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorSingleInFlightModelCallChildIntegrationTests
{
    [Fact]
    public async Task A_child_thread_never_has_two_model_calls_in_flight_even_if_scheduled_twice()
    {
        var sessionId = "sess_single_inflight_child";
        var root = Path.Combine(Path.GetTempPath(), "harness-single-inflight-child", Guid.NewGuid().ToString("N"));

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
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions { CommitAssistantTextDeltas = false },
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray.Create(ToolSchemas.ReportIntent));

        // Create a child thread from an empty main snapshot.
        var childId = "thr_child_inflight";
        await orchestrator.RequestForkChildThreadAsync(
            parentThreadId: ThreadIds.Main,
            childThreadId: childId,
            seedCommitted: ImmutableArray<SessionEvent>.Empty,
            cancellationToken: CancellationToken.None);

        // Enqueue work for the child.
        await orchestrator.ObserveAsync(childId, new ObservedUserMessage("hi"));

        // Schedule twice (duplicate wake).
        orchestrator.ScheduleRun(childId);
        orchestrator.ScheduleRun(childId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = orchestrator.RunUntilQuiescentAsync(cts.Token);

        await chat.FirstCallStarted.Task.WaitAsync(cts.Token);
        chat.ConcurrentCallsMax.Should().Be(1);

        chat.AllowCompletion.TrySetResult(true);
        await runTask;

        chat.ConcurrentCallsMax.Should().Be(1);
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

    private sealed class BlockingChatClient : MeaiIChatClient
    {
        private int _inFlight;
        public int ConcurrentCallsMax { get; private set; }

        public TaskCompletionSource<bool> FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            MeaiChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            ConcurrentCallsMax = Math.Max(ConcurrentCallsMax, now);
            FirstCallStarted.TrySetResult(true);

            try
            {
                await AllowCompletion.Task.WaitAsync(cancellationToken);
                yield break;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
