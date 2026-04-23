using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CompactionContinuationIntegrationTests
{
    [Fact]
    public async Task RunWithEffectsAsync_WhenCompactionDueAfterTools_CompactsBetweenTurns_ThenContinuesWithFollowUpModelCall()
    {
        var opts = new CoreOptions(
            ContextWindowTokensByProviderModel: _ => 1000,
            CompactionThreshold: 0.90);

        var initial = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent),
        };

        var exec = new ScriptedExecutor();

        var committed = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunWithEffectsAsync(
                           initial,
                           Observed(),
                           effects: exec,
                           options: opts,
                           cancellationToken: CancellationToken.None))
        {
            committed.Add(e);
        }

        // Follow-up should happen: initial model call, compaction, then follow-up model call.
        exec.CallModelCount.Should().Be(2);
        exec.Sequence.Should().ContainInOrder(
            "call_model#1",
            "check_permission:report_intent",
            "execute_tool:report_intent",
            "run_compaction",
            "call_model#2");

        // Boundary invariant: TurnEnded -> CompactionCommitted -> TurnStarted (no other committed between).
        var iTurnEnded = committed.FindIndex(e => e is TurnEnded);
        iTurnEnded.Should().BeGreaterThanOrEqualTo(0);

        var iCompaction = committed.FindIndex(e => e is CompactionCommitted);
        iCompaction.Should().Be(iTurnEnded + 1);

        var iTurnStarted2 = committed.FindIndex(iCompaction + 1, e => e is TurnStarted);
        iTurnStarted2.Should().Be(iCompaction + 1);

        committed.Should().Contain(e => e is TurnEnded); // final end exists too

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
                    return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "ok")));

                case ExecuteToolCall t:
                    Sequence.Add($"execute_tool:{t.ToolName}");
                    return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, new { ok = true })));

                case RunCompaction:
                    Sequence.Add("run_compaction");
                    return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                        new ObservedCompactionGenerated(System.Text.Json.JsonSerializer.SerializeToElement(new { summary = "s" }), "s")));

                default:
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
                // Model emits a tool intent and then reports high usage (trigger compaction).
                yield return new ObservedToolCallDetected("call_0", "report_intent", new { intent = "x" });
                yield return new ObservedAssistantMessageCompleted();
                yield return new ObservedTokenUsage(InputTokens: 100, OutputTokens: 900, TotalTokens: 900, ProviderModel: "p");
                yield break;
            }

            // Follow-up after compaction.
            yield return new ObservedAssistantTextDelta("DONE");
            yield return new ObservedAssistantMessageCompleted();
            await Task.CompletedTask;
        }
    }
}
