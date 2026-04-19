using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolFailureRecoveryTests
{
    [Fact]
    public async Task RunWithEffectsAsync_WhenToolFails_CallsModelAgain_AndCommitsAssistantMessage()
    {
        var initial = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
        };

        var exec = new ToolFailThenRecoverExecutor();

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            yield return new ObservedUserMessage("hi");
        }

        var committed = new List<SessionEvent>();
        await foreach (var e in TurnRunner.RunWithEffectsAsync(
            initial,
            Observed(),
            effects: exec,
            sink: new CollectingSink(committed),
            options: new CoreOptions { CommitAssistantTextDeltas = false },
            cancellationToken: CancellationToken.None))
        {
            // drain
        }

        exec.Executed.Should().ContainInOrder(
            "effect:call_model#1",
            "effect:check_permission:call_0",
            "effect:check_permission:call_1",
            "effect:execute_tool:call_0",
            "effect:execute_tool:call_1",
            "effect:call_model#2");

        committed.OfType<ToolCallFailed>().Single(f => f.ToolId == "call_1").Error.Should().Be("boom");
        committed.OfType<AssistantMessage>().Single().Text.Should().Be("Recovered");
    }

    private sealed class CollectingSink(List<SessionEvent> committed) : IEventSink
    {
        public ValueTask OnObservedAsync(ObservedChatEvent evt, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask OnCommittedAsync(SessionEvent evt, CancellationToken cancellationToken)
        {
            committed.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ToolFailThenRecoverExecutor : IStreamingEffectExecutor
    {
        private int _callModelCount;
        public List<string> Executed { get; } = new();

        public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
        {
            var list = new List<ObservedChatEvent>();
            await foreach (var o in ExecuteStreamingAsync(state, effect, cancellationToken))
                list.Add(o);
            return list.ToImmutableArray();
        }

        public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            switch (effect)
            {
                case CallModel:
                {
                    _callModelCount++;
                    Executed.Add($"effect:call_model#{_callModelCount}");

                    if (_callModelCount == 1)
                    {
                        // Model proposes intent + a tool call.
                        yield return new ObservedToolCallDetected("call_0", "report_intent", new { intent = "read" });
                        yield return new ObservedToolCallDetected("call_1", "read_text_file", new { path = "/tmp/a.txt" });
                        yield return new ObservedAssistantMessageCompleted();
                        yield break;
                    }

                    // Recovery model response after tool failure.
                    yield return new ObservedAssistantTextDelta("Recovered");
                    yield return new ObservedAssistantMessageCompleted();
                    yield break;
                }

                case CheckPermission p:
                    Executed.Add($"effect:check_permission:{p.ToolId}");
                    yield return new ObservedPermissionApproved(p.ToolId, "ok");
                    yield break;

                case ExecuteToolCall t:
                    Executed.Add($"effect:execute_tool:{t.ToolId}");

                    if (t.ToolId == "call_1")
                        yield return new ObservedToolCallFailed(t.ToolId, "boom");
                    else
                        yield return new ObservedToolCallCompleted(t.ToolId, new { ok = true });

                    yield break;

                default:
                    yield break;
            }
        }
    }
}
