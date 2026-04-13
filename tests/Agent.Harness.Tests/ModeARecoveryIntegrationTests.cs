using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.TitleGeneration;

namespace Agent.Harness.Tests;

public sealed class ModeARecoveryIntegrationTests
{
    [Fact]
    public async Task RunTurnAsync_ToolRejected_InvalidArgs_RePrompts_And_ProducesAssistantMessage()
    {
        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReadTextFile),
        };

        var effects = new InvalidArgsThenAnswerEffectExecutor();
        var runner = new SessionRunner(new CoreOptions(), new SessionTitleGenerator(new NullChatClient()), effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Read /tmp/a.txt");
        }

        var result = await runner.RunTurnAsync(state, Observed(), CancellationToken.None);

        Assert.Contains(result.NewlyCommitted, e => e is ToolCallRejected { ToolId: "call_1", Reason: "invalid_args" });
        Assert.Contains(result.NewlyCommitted, e => e is AssistantMessage { Text: "Ok." });
        Assert.Contains(result.NewlyCommitted, e => e is TurnEnded);

        // We should have prompted the model twice.
        Assert.Equal(2, effects.Executed.Count(e => e is CallModel));
    }

    private sealed class InvalidArgsThenAnswerEffectExecutor : IEffectExecutor
    {
        private int _modelCalls;
        public List<Effect> Executed { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            Executed.Add(effect);

            return effect switch
            {
                CallModel => Task.FromResult(ModelStep()),
                _ => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty),
            };
        }

        private ImmutableArray<ObservedChatEvent> ModelStep()
        {
            _modelCalls++;

            if (_modelCalls == 1)
            {
                // Missing required "path" -> rejected by reducer with invalid_args.
                return ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedToolCallDetected("call_1", "read_text_file", new { }));
            }

            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedAssistantTextDelta("Ok."),
                new ObservedAssistantMessageCompleted());
        }
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }
}
