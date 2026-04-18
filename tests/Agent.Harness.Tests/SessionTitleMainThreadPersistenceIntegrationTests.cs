using System.Collections.Immutable;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using Agent.Harness.TitleGeneration;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class SessionTitleMainThreadPersistenceIntegrationTests
{
    private sealed class AnsweringEffects(string assistantText) : IStreamingEffectExecutor
    {
        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
            => throw new InvalidOperationException("streaming_only");

        public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (effect is CallModel)
            {
                yield return new ObservedAssistantTextDelta(assistantText);
                yield return new ObservedAssistantMessageCompleted(null);
                yield break;
            }

            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ScriptedMeaiChatClient : IChatClient
    {
        private readonly string _assistantText;

        public ScriptedMeaiChatClient(string assistantText)
        {
            _assistantText = assistantText;
        }

        public List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> Calls { get; } = new();

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add(list);

            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, _assistantText)
            }));
        }

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task Main_thread_sets_session_json_title_after_first_turn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);
        var threads = new JsonlThreadStore(dir);

        var sessionId = "sess1";

        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:01Z"));

        threads.CreateMainIfMissing(sessionId);

        var chat = new ScriptedMeaiChatClient("My Title");
        var titleGen = new SessionTitleGenerator(chat);
        var effects = new AnsweringEffects("Hello");
        var runner = new SessionRunner(new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false), titleGen, effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Hi");
            await Task.Yield();
        }

        var sink = new MainThreadEventSink(sessionId, threads, store, logObserved: false);

        await runner.RunTurnAsync(ThreadIds.Main, SessionState.Empty, Observed(), CancellationToken.None, sink: sink);

        var meta = store.TryLoadMetadata(sessionId);
        meta.Should().NotBeNull();
        meta!.Title.Should().Be("My Title");

        threads.LoadCommittedEvents(sessionId, ThreadIds.Main).Should().Contain(new SessionTitleSet("My Title"));
    }

    [Fact]
    public async Task Child_thread_does_not_set_session_title()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-store-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(dir);
        var threads = new JsonlThreadStore(dir);

        var sessionId = "sess1";

        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: "2026-04-12T00:00:00Z",
            UpdatedAtIso: "2026-04-12T00:00:01Z"));

        threads.CreateMainIfMissing(sessionId);

        var chat = new ScriptedMeaiChatClient("SHOULD_NOT_BE_USED");
        var titleGen = new SessionTitleGenerator(chat);
        var effects = new AnsweringEffects("Hello");
        var runner = new SessionRunner(new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false), titleGen, effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Hi");
            await Task.Yield();
        }

        // Child thread sink (committed events go to the child thread log only).
        var childThreadId = "thr_child";
        threads.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: childThreadId,
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Model: null));

        var sink = new ThreadEventSink(sessionId, childThreadId, threads);

        await runner.RunTurnAsync(childThreadId, SessionState.Empty, Observed(), CancellationToken.None, sink: sink);

        // Title should remain null.
        store.TryLoadMetadata(sessionId)!.Title.Should().BeNull();

        // And title generator should not have been called.
        chat.Calls.Should().BeEmpty();

        threads.LoadCommittedEvents(sessionId, childThreadId).OfType<SessionTitleSet>().Should().BeEmpty();
    }
}
