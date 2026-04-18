using System.Collections.Immutable;
using Agent.Harness.TitleGeneration;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class SessionRunnerTitleIntegrationTests
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
        private string _next = "";

        public List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> Calls { get; } = new();

        public ScriptedMeaiChatClient WhenCalledReturn(string assistantText)
        {
            _next = assistantText;
            return this;
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add(list);

            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, _next)
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
    public async Task RunTurn_commits_title_event_after_first_turn()
    {
        var coreOptions = new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false);
        var chat = new ScriptedMeaiChatClient().WhenCalledReturn("My Title");
        var titleGen = new SessionTitleGenerator(chat);
        var effects = new AnsweringEffects("Hello");
        var runner = new SessionRunner(coreOptions, titleGen, effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Hi");
            await Task.Yield();
        }

        var result = await runner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, SessionState.Empty, Observed(), CancellationToken.None);

        result.NewlyCommitted.Should().ContainInOrder(
            new UserMessage("Hi"),
            new AssistantMessage("Hello"),
            new SessionTitleSet("My Title"));

        result.Next.Committed.Should().Contain(new SessionTitleSet("My Title"));
    }

    [Fact]
    public async Task RunTurn_does_not_generate_title_if_already_present()
    {
        var coreOptions = new CoreOptions(CommitAssistantTextDeltas: false, CommitReasoningTextDeltas: false);
        var chat = new ScriptedMeaiChatClient().WhenCalledReturn("SHOULD_NOT_BE_USED");
        var titleGen = new SessionTitleGenerator(chat);
        var effects = new AnsweringEffects("Ok");
        var runner = new SessionRunner(coreOptions, titleGen, effects);

        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Hi"),
                new AssistantMessage("Hello"),
                new SessionTitleSet("Existing")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Next");
            await Task.Yield();
        }

        var result = await runner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, initial, Observed(), CancellationToken.None);

        result.NewlyCommitted.OfType<SessionTitleSet>().Should().NotContain(st => st.Title == "SHOULD_NOT_BE_USED");
        chat.Calls.Should().BeEmpty();
    }
}
