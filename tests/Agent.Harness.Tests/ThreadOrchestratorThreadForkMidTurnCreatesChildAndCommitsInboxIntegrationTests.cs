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
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorThreadStartForkMidTurnCreatesChildAndCommitsInboxIntegrationTests
{
    [Fact]
    public async Task ThreadStart_fork_called_during_main_turn_creates_child_and_commits_child_inbox_message()
    {
        var sessionId = "sess_midturn_thread_start_fork";
        var root = Path.Combine(Path.GetTempPath(), "harness-midturn-thread-fork", Guid.NewGuid().ToString("N"));

        var sessionStore = new JsonlSessionStore(root);
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threadStore = new JsonlThreadStore(root);
        var threads = new ThreadManager(sessionId, threadStore);

        var chat = new ThreadStartForkChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions { CommitAssistantTextDeltas = true },
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threadAppender: threadStore,
            threads: threads);

        orchestrator.SetToolCatalog(ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            ToolSchemas.ThreadStart));

        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedUserMessage("hi"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await orchestrator.RunUntilQuiescentAsync(cts.Token);

        var child = threadStore.ListThreads(sessionId)
            .Single(t => t.ThreadId != ThreadIds.Main && t.ParentThreadId == ThreadIds.Main);

        var childCommitted = threadStore.LoadCommittedEvents(sessionId, child.ThreadId);

        childCommitted.OfType<UserMessage>()
            .Should().Contain(m => m.Text == "hi");

        childCommitted.OfType<ThreadInboxMessageEnqueued>()
            .Should().Contain(e => e.Kind == ThreadInboxMessageKind.InterThreadMessage && e.Text.Contains("child hello"));
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

    private sealed class ThreadStartForkChatClient : MeaiIChatClient
    {
        private bool _toolsDone;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!_toolsDone)
            {
                _toolsDone = true;
                return Tools();
            }

            return Text();

            async IAsyncEnumerable<MeaiChatResponseUpdate> Tools()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "thread fork" }),
                        new MeaiFunctionCallContent("call_1", "thread_start", new Dictionary<string, object?>
                        {
                            ["context"] = "fork",
                            ["message"] = "child hello",
                            ["delivery"] = "immediate",
                        }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Text()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ok") } };
                await Task.CompletedTask;
            }
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
