using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Tests.TestChatClients;
using Agent.Harness.TitleGeneration;

namespace Agent.Harness.Tests;

public sealed class ModeARecoveryIntegrationTests
{
    [Fact]
    public async Task RunTurnAsync_ToolRejected_InvalidArgs_RePrompts_And_ProducesAssistantMessage()
    {
        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
        };

        var effects = new InvalidArgsThenAnswerEffectExecutor();
        var runner = new SessionRunner(new CoreOptions(), new SessionTitleGenerator(new FixedResponseChatClient("Some title")), effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Read /tmp/a.txt");
        }

        var result = await runner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, state, Observed(), CancellationToken.None);

        Assert.Contains(result.NewlyCommitted, e => e is ToolCallRejected { ToolId: "call_1", Reason: "invalid_args" });
        Assert.Contains(result.NewlyCommitted, e => e is AssistantMessage { Text: "Ok." });
        Assert.Contains(result.NewlyCommitted, e => e is TurnEnded);

        // We should have prompted the model twice.
        Assert.Equal(2, effects.Executed.Count(e => e is CallModel));
    }

    private sealed class InvalidArgsThenAnswerEffectExecutor : IStreamingEffectExecutor
    {
        private int _modelCalls;
        public List<Effect> Executed { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            Executed.Add(effect);

            if (effect is CallModel)
                throw new InvalidOperationException("call_model_must_be_streamed_in_tests");

            return Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
        }

        public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Executed.Add(effect);

            if (effect is not CallModel)
                yield break;

            foreach (var o in ModelStep())
                yield return o;

            await Task.CompletedTask;
        }

        private ImmutableArray<ObservedChatEvent> ModelStep()
        {
            _modelCalls++;

            if (_modelCalls == 1)
            {
                // Missing required "path" -> rejected by reducer with invalid_args.
                return ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedToolCallDetected("call_0", "report_intent", new { intent = "read a file" }),
                    new ObservedToolCallDetected("call_1", "read_text_file", new { }));
            }

            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedAssistantTextDelta("Ok."),
                new ObservedAssistantMessageCompleted());
        }
    }

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
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "")
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
}
