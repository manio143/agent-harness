using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.Tests.TestChatClients;
using Agent.Harness.TitleGeneration;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ModeATurnIntegrationTests
{
    [Fact]
    public async Task RunTurnAsync_ModeA_ToolIntent_ExecutesTool_RePrompts_And_EndsTurn()
    {
        // WHY THIS IS AN INVARIANT:
        // In Mode A, the model may emit only tool-call intent first. The harness must:
        // - commit user message
        // - CallModel
        // - detect tool intent -> permission -> execute tool
        // - CallModel again
        // - commit assistant output
        // - commit TurnEnded when stabilized

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
        };

        var effects = new ScriptedEffectExecutor();
        var runner = new SessionRunner(new CoreOptions(), new SessionTitleGenerator(new FixedResponseChatClient("Some title")), effects);

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("Read /tmp/a.txt");
        }

        var result = await runner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, state, Observed(), CancellationToken.None);

        // We should start and end the turn via core-committed events.
        Assert.Contains(result.NewlyCommitted, e => e is TurnStarted);
        Assert.Contains(result.NewlyCommitted, e => e is TurnEnded);

        // Tool lifecycle committed.
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallRequested { ToolId: "call_1", ToolName: "read_text_file" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallPermissionApproved { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallPending { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallCompleted { ToolId: "call_1" });

        // Assistant output committed after the second model call.
        Assert.Contains(result.NewlyCommitted, e => e is AssistantMessage { Text: "Done." });

        // Effects executed in the expected Mode A order.
        // Note: report_intent is required before other tools.
        effects.Executed.Select(e => e.GetType()).Should().ContainInOrder(
            typeof(CallModel),
            typeof(CheckPermission),   // report_intent
            typeof(CheckPermission),   // read_text_file (queued before report_intent execution)
            typeof(ExecuteToolCall),   // report_intent
            typeof(ExecuteToolCall),   // read_text_file
            typeof(CallModel));
    }

    private sealed class ScriptedEffectExecutor : IStreamingEffectExecutor
    {
        private int _modelCalls;
        public List<Effect> Executed { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            Executed.Add(effect);

            if (effect is CallModel)
                throw new InvalidOperationException("call_model_must_be_streamed_in_tests");

            return effect switch
            {

                CheckPermission p => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedPermissionApproved(p.ToolId, "capability_present"))),

                ExecuteToolCall t => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedToolCallProgressUpdate(t.ToolId, new { text = "running" }),
                    new ObservedToolCallCompleted(t.ToolId, new { content = "hello" }))),

                _ => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty),
            };
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

            // First model call: report intent + tool intent.
            if (_modelCalls == 1)
            {
                return ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedToolCallDetected("call_0", "report_intent", new { intent = "read /tmp/a.txt" }),
                    new ObservedToolCallDetected("call_1", "read_text_file", new { path = "/tmp/a.txt" }));
            }

            // Second model call: final assistant message.
            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedAssistantTextDelta("Done."),
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
