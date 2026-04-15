using System.Collections.Immutable;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class StreamingCallModelBoundaryTests
{
    [Fact]
    public async Task RunWithEffectsAsync_Streams_AssistantDeltas_Before_Running_ToolEffects()
    {
        var initial = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent, ToolSchemas.ReadTextFile),
        };

        var sink = new CapturingSink();
        var exec = new StreamingThenToolEffectExecutor();

        async IAsyncEnumerable<ObservedChatEvent> Observed()
        {
            // User triggers model call.
            yield return new ObservedUserMessage("hi");
        }

        await foreach (var _ in TurnRunner.RunWithEffectsAsync(
            initial,
            Observed(),
            exec,
            sink: sink,
            options: new CoreOptions { CommitAssistantTextDeltas = true },
            cancellationToken: CancellationToken.None))
        {
            // drain
        }

        // Ensure we observed/committed assistant delta before any tool execution was attempted.
        sink.Order.Should().ContainInOrder(
            "observed:assistant_delta",
            "committed:assistant_delta");

        exec.Executed.Should().ContainInOrder(
            "effect:call_model",
            "effect:check_permission",
            "effect:execute_tool");
    }

    private sealed class CapturingSink : IEventSink
    {
        public List<string> Order { get; } = new();

        public ValueTask OnObservedAsync(ObservedChatEvent evt, CancellationToken cancellationToken)
        {
            if (evt is ObservedAssistantTextDelta)
                Order.Add("observed:assistant_delta");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnCommittedAsync(SessionEvent evt, CancellationToken cancellationToken)
        {
            if (evt is AssistantTextDelta)
                Order.Add("committed:assistant_delta");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamingThenToolEffectExecutor : IStreamingEffectExecutor
    {
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
                    Executed.Add("effect:call_model");
                    // Stream a delta first...
                    yield return new ObservedAssistantTextDelta("Hello");
                    // ...then propose tool calls and finish.
                    yield return new ObservedToolCallDetected("call_0", "report_intent", new { intent = "read file" });
                    yield return new ObservedToolCallDetected("call_1", "read_text_file", new { path = "/tmp/a.txt" });
                    yield return new ObservedAssistantMessageCompleted();
                    yield break;

                case CheckPermission:
                    Executed.Add("effect:check_permission");
                    yield return new ObservedPermissionApproved(((CheckPermission)effect).ToolId, "capability_present");
                    yield break;

                case ExecuteToolCall:
                    Executed.Add("effect:execute_tool");
                    yield return new ObservedToolCallCompleted(((ExecuteToolCall)effect).ToolId, new { ok = true });
                    yield break;

                default:
                    yield break;
            }
        }
    }
}
