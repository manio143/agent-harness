using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class TurnRunnerMultiToolCallIntegrationTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task RunWithEffectsAsync_WhenModelEmitsMultipleToolCalls_WaitsUntilAllCompleteBeforeRePrompting()
    {
        var initial = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
        };

        var exec = new ScriptedExecutor();

        var committed = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunWithEffectsAsync(
                           initial,
                           Observed(),
                           effects: exec,
                           cancellationToken: CancellationToken.None))
        {
            committed.Add(e);
        }

        // ASSERT: model was called twice (initial tool-intent, then follow-up after both tool results).
        exec.CallModelCount.Should().Be(2);

        // ASSERT: we executed BOTH tools before the second model call.
        exec.Sequence.Should().ContainInOrder(
            "call_model#1",
            "check_permission:report_intent",
            "check_permission:read_text_file",
            "execute_tool:report_intent",
            "execute_tool:read_text_file",
            "call_model#2");

        // And we should see exactly one TurnEnded.
        committed.OfType<TurnEnded>().Should().ContainSingle();

        static async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedTurnStarted(Agent.Harness.Threads.ThreadIds.Main);
            yield return new ObservedUserMessage("Hi");
            await Task.CompletedTask;
        }
    }


    private sealed class ScriptedExecutor : IStreamingEffectExecutor
    {
        public int CallModelCount { get; private set; }
        public List<string> Sequence { get; } = new();

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            switch (effect)
            {
                case CheckPermission p:
                    Sequence.Add($"check_permission:{p.ToolName}");
                    return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "capability_present")));

                case ExecuteToolCall t:
                    Sequence.Add($"execute_tool:{t.ToolName}");
                    return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, new { ok = true })));

                default:
                    Sequence.Add(effect.GetType().Name);
                    return Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
            }
        }

        public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (effect is not CallModel)
                yield break;

            CallModelCount++;
            Sequence.Add($"call_model#{CallModelCount}");

            if (CallModelCount == 1)
            {
                // Tool intents: report_intent must be observed before other tools.
                yield return new ObservedToolCallDetected("call_0", "report_intent", new { intent = "x" });
                yield return new ObservedToolCallDetected("call_1", "read_text_file", new { path = "/tmp/a.txt" });
                yield return new ObservedAssistantMessageCompleted();
                yield break;
            }

            // Follow-up model call: just end the turn.
            yield return new ObservedAssistantTextDelta("DONE");
            yield return new ObservedAssistantMessageCompleted();
            await Task.CompletedTask;
        }
    }
}
