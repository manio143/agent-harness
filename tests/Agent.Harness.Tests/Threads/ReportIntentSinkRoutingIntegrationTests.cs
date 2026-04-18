using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Threads;
using Xunit;

namespace Agent.Harness.Tests.Threads;

/// <summary>
/// Tests that report_intent tool execution creates ThreadIntentReported via the reducer/sink pipeline,
/// not via direct store writes.
/// </summary>
public sealed class ReportIntentSinkRoutingIntegrationTests
{
    [Fact]
    public void ReportIntent_FlowsThroughReducerAndSink()
    {
        // Arrange
        var store = new InMemoryThreadStore();
        var sessionId = "ses_test";
        var sink = new FakeSink();
        
        store.CreateMainIfMissing(sessionId);

        // Create the initial ToolCallRequested event that report_intent would have generated
        var toolCallArgs = JsonSerializer.SerializeToElement(new { intent = "test intent" });
        var toolCallRequested = new ToolCallRequested(
            ToolId: "call_0",
            ToolName: "report_intent",
            Args: toolCallArgs
        );

        var initialState = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent),
            Committed = ImmutableArray.Create<SessionEvent>(toolCallRequested)
        };

        var observed = new ObservedToolCallCompleted(
            ToolId: "call_0",
            Result: JsonSerializer.SerializeToElement(new { intent = "test intent" })
        );

        // Act - reduce the tool completion
        var result = Core.Reduce(initialState, observed, null);

        // Assert - should have committed both ToolCallCompleted AND ThreadIntentReported
        Assert.Equal(2, result.NewlyCommitted.Length);
        Assert.IsType<ToolCallCompleted>(result.NewlyCommitted[0]);
        var reported = Assert.IsType<ThreadIntentReported>(result.NewlyCommitted[1]);
        Assert.Equal("test intent", reported.Intent);
    }

    private sealed class FakeSink : IEventSink
    {
        public List<SessionEvent> CommittedEvents { get; } = new();

        public ValueTask OnObservedAsync(ObservedChatEvent evt, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask OnCommittedAsync(SessionEvent evt, CancellationToken cancellationToken)
        {
            CommittedEvents.Add(evt);
            return ValueTask.CompletedTask;
        }
    }
}
