using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.TitleGeneration;

namespace Agent.Harness.Tests;

public sealed class ModeAToolFailureRecoveryIntegrationTests
{
    [Fact]
    public async Task RunTurnAsync_ToolFailed_RePrompts_And_ProducesAssistantMessage()
    {
        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
        };

        var effects = new ToolFailsThenAnswerEffectExecutor();
        var runner = new SessionRunner(new CoreOptions(), new SessionTitleGenerator(new NullChatClient()), effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Read /tmp/a.txt");
        }

        var result = await runner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, state, Observed(), CancellationToken.None);

        Assert.Contains(result.NewlyCommitted, e => e is ToolCallRequested { ToolId: "call_1", ToolName: "read_text_file" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallFailed { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is AssistantMessage { Text: "Ok." });
        Assert.Contains(result.NewlyCommitted, e => e is TurnEnded);

        // We should have prompted the model twice.
        Assert.Equal(2, effects.Executed.Count(e => e is CallModel));
    }

    private sealed class ToolFailsThenAnswerEffectExecutor : IEffectExecutor
    {
        private int _modelCalls;
        public List<Effect> Executed { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            Executed.Add(effect);

            return effect switch
            {
                CallModel => Task.FromResult(ModelStep()),

                CheckPermission p => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedPermissionApproved(p.ToolId, "capability_present"))),

                ExecuteToolCall t => Task.FromResult(t.ToolName == "report_intent"
                    ? ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, new { ok = true }))
                    : ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(t.ToolId, "boom"))),

                _ => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty),
            };
        }

        private ImmutableArray<ObservedChatEvent> ModelStep()
        {
            _modelCalls++;

            if (_modelCalls == 1)
            {
                return ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedToolCallDetected("call_0", "report_intent", new { intent = "read a file" }),
                    new ObservedToolCallDetected("call_1", "read_text_file", new { path = "/tmp/a.txt" }));
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
