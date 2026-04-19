using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class AcpSessionAgentAdapterToolCallLifecycleTests
{
    [Fact]
    public async Task PromptAsync_ToolCallUpdate_NonStringContent_UsesRawJsonText()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            yield return new ObservedToolCallDetected(
                ToolId: "1",
                ToolName: ToolSchemas.ReportIntent.Name,
                Args: new { intent = "do" });

            yield return new ObservedToolCallProgressUpdate(
                ToolId: "1",
                Content: new { foo = "bar" });

            yield return new ObservedToolCallCompleted(
                ToolId: "1",
                Result: new { ok = true });

            await Task.CompletedTask;
        }

        var events = new CapturingSessionEvents();
        var turn = new FakeTurn();

        var adapter = new AcpSessionAgentAdapter(
            sessionId: "s1",
            events: events,
            observed: Observed,
            toolCatalog: ImmutableArray.Create(ToolSchemas.ReportIntent));

        var resp = await adapter.PromptAsync(
            new PromptRequest { SessionId = "s1", Prompt = new List<ContentBlock> { new TextContent { Text = "hi" } } },
            turn,
            CancellationToken.None);

        resp.StopReason.Value.Should().Be(StopReason.EndTurn);

        turn.ToolCalls.Started.Should().ContainKey("1");
        var call = turn.ToolCalls.Started["1"];

        call.InProgressCalls.Should().Be(1);
        call.CompletedCalls.Should().Be(1);
        call.FailedCalls.Should().Be(0);

        call.AddedContents.Should().HaveCount(1);
        var text = call.AddedContents.Single().As<ToolCallContentContent>().Content.As<TextContent>().Text;

        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("foo").GetString().Should().Be("bar");
    }

    [Fact]
    public async Task PromptAsync_ToolCallRejected_WithDetails_MapsToFailedToolCall()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            // Missing report_intent policy triggers ToolCallRejected with details.
            yield return new ObservedTurnStarted("t1");

            yield return new ObservedToolCallDetected(
                ToolId: "1",
                ToolName: "read_text_file",
                Args: new { path = "demo.txt" });

            await Task.CompletedTask;
        }

        var events = new CapturingSessionEvents();
        var turn = new FakeTurn();

        var adapter = new AcpSessionAgentAdapter(
            sessionId: "s1",
            events: events,
            observed: Observed);

        await adapter.PromptAsync(
            new PromptRequest { SessionId = "s1", Prompt = new List<ContentBlock>() },
            turn,
            CancellationToken.None);

        turn.ToolCalls.Started.Should().ContainKey("1");
        var call = turn.ToolCalls.Started["1"];

        call.Title.Should().Be("rejected");
        call.FailedCalls.Should().Be(1);
        call.FailedMessages.Single().Should().Be("missing_report_intent: must_call:report_intent");
    }

    [Fact]
    public async Task PromptAsync_WhenTurnReportsActiveToolCalls_CancelsAndClearsDanglingCalls()
    {
        static async IAsyncEnumerable<ObservedChatEvent> First(PromptRequest _)
        {
            yield return new ObservedToolCallDetected(
                ToolId: "1",
                ToolName: ToolSchemas.ReportIntent.Name,
                Args: new { intent = "do" });
            await Task.CompletedTask;
        }

        static async IAsyncEnumerable<ObservedChatEvent> Second(PromptRequest _)
        {
            // If adapter did not clear its internal tool call map, this would call InProgress/AddContent on the old call.
            yield return new ObservedToolCallProgressUpdate("1", new { message = "x" });
            await Task.CompletedTask;
        }

        Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> current = First;
        IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest r) => current(r);

        var events = new CapturingSessionEvents();
        var adapter = new AcpSessionAgentAdapter(
            sessionId: "s1",
            events: events,
            observed: Observed,
            toolCatalog: ImmutableArray.Create(ToolSchemas.ReportIntent));

        var turn1 = new FakeTurn();
        await adapter.PromptAsync(new PromptRequest { SessionId = "s1", Prompt = new List<ContentBlock>() }, turn1, CancellationToken.None);
        var call = turn1.ToolCalls.Started["1"];

        // Next prompt: simulate ACP telling us there are still active tool calls.
        var turn2 = new FakeTurn();
        turn2.ToolCalls.ActiveIds.Add("1");
        turn2.ToolCalls.CancelAllCalls.Should().Be(0);

        current = Second;
        await adapter.PromptAsync(new PromptRequest { SessionId = "s1", Prompt = new List<ContentBlock>() }, turn2, CancellationToken.None);

        turn2.ToolCalls.CancelAllCalls.Should().Be(1);

        // Ensure the original call from turn1 was not mutated by the second prompt.
        call.InProgressCalls.Should().Be(0);
        call.AddedContents.Should().BeEmpty();
    }

    private sealed class CapturingSessionEvents : IAcpSessionEvents
    {
        public List<object> Updates { get; } = new();

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTurn : IAcpPromptTurn
    {
        public FakeToolCalls ToolCallsImpl { get; } = new();

        IAcpToolCalls IAcpPromptTurn.ToolCalls => ToolCallsImpl;

        public FakeToolCalls ToolCalls => ToolCallsImpl;
    }

    private sealed class FakeToolCalls : IAcpToolCalls
    {
        public Dictionary<string, FakeToolCall> Started { get; } = new();
        public List<string> ActiveIds { get; } = new();
        public int CancelAllCalls { get; private set; }

        public IReadOnlyCollection<string> ActiveToolCallIds => ActiveIds;

        public IAcpToolCall Start(string toolCallId, string title, ToolKind kind)
        {
            var call = new FakeToolCall(toolCallId, title, kind);
            Started[toolCallId] = call;
            if (!ActiveIds.Contains(toolCallId))
                ActiveIds.Add(toolCallId);
            return call;
        }

        public Task CancelAllAsync(CancellationToken cancellationToken = default)
        {
            CancelAllCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeToolCall : IAcpToolCall
    {
        public FakeToolCall(string id, string title, ToolKind kind)
        {
            ToolCallId = id;
            Title = title;
            Kind = kind;
        }

        public string ToolCallId { get; }
        public string Title { get; }
        public ToolKind Kind { get; }

        public int InProgressCalls { get; private set; }
        public int CompletedCalls { get; private set; }
        public int FailedCalls { get; private set; }
        public int CancelledCalls { get; private set; }

        public List<string> FailedMessages { get; } = new();
        public List<ToolCallContent> AddedContents { get; } = new();
        public object? CompletedRawOutput { get; private set; }

        public Task AddContentAsync(ToolCallContent content, CancellationToken cancellationToken = default)
        {
            AddedContents.Add(content);
            return Task.CompletedTask;
        }

        public Task InProgressAsync(CancellationToken cancellationToken = default)
        {
            InProgressCalls++;
            return Task.CompletedTask;
        }

        public Task CompletedAsync(CancellationToken cancellationToken = default, object? rawOutput = null)
        {
            CompletedCalls++;
            CompletedRawOutput = rawOutput;
            return Task.CompletedTask;
        }

        public Task FailedAsync(string message, CancellationToken cancellationToken = default)
        {
            FailedCalls++;
            FailedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task CancelledAsync(CancellationToken cancellationToken = default)
        {
            CancelledCalls++;
            return Task.CompletedTask;
        }
    }
}

file static class ToolContentExtensions
{
    public static T As<T>(this object o) where T : class => (T)o;
}
