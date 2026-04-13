using System.Collections.Immutable;
using Agent.Harness;
using Agent.Harness.TitleGeneration;

namespace Agent.Harness.Tests;

public class SessionRunnerEffectLoopTests
{
    [Fact]
    public async Task RunTurnAsync_Executes_Effects_And_Feeds_Observations_Back_Into_Reducer()
    {
        // WHY THIS IS AN INVARIANT:
        // Tool calling requires a reducer/effects loop: reducer emits effects, imperative shell executes
        // them and feeds observations back, and only the reducer commits.

        var state = SessionState.Empty;

        var effects = new FakeEffectExecutor();
        var runner = new SessionRunner(new CoreOptions(), new SessionTitleGenerator(new ThrowingChatClient()), effects);

        // External observed stream: the model proposes a tool call.
        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedToolCallDetected("call_1", "read_text_file", new { path = "/tmp/a.txt" });
        }

        var result = await runner.RunTurnAsync(state, Observed(), CancellationToken.None);

        // ASSERT: we committed through the whole lifecycle.
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallRequested { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallPermissionApproved { ToolId: "call_1", Reason: "capability_present" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallPending { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallInProgress { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallUpdate { ToolId: "call_1" });
        Assert.Contains(result.NewlyCommitted, e => e is ToolCallCompleted { ToolId: "call_1" });

        // ASSERT: effects were executed.
        Assert.Equal(2, effects.Executed.Count);
        Assert.IsType<CheckPermission>(effects.Executed[0]);
        Assert.IsType<ExecuteToolCall>(effects.Executed[1]);
    }

    private sealed class FakeEffectExecutor : IEffectExecutor
    {
        public List<Effect> Executed { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            Executed.Add(effect);

            return effect switch
            {
                CheckPermission perm => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedPermissionApproved(perm.ToolId, "capability_present"))),

                ExecuteToolCall exec => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                    new ObservedToolCallProgressUpdate(exec.ToolId, new { text = "running" }),
                    new ObservedToolCallCompleted(exec.ToolId, new { ok = true }))),

                _ => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty),
            };
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> renderedMessages, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Title generation should not run in this test.");
    }
}
