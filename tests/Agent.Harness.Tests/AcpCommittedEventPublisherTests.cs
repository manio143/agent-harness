using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class AcpCommittedEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_AssistantMessage_Publishes_WhenNotDeltaCommitMode()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(
            events,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: false),
            publishOptions: new AcpPublishOptions());

        await publisher.PublishAsync(new AssistantMessage("hello"), new FakeTurn(), new Dictionary<string, IAcpToolCall>(), CancellationToken.None);

        events.Updates.Should().ContainSingle()
            .Which.Should().BeOfType<AgentMessageChunk>()
            .Which.Content.As<TextContent>().Text.Should().Be("hello");
    }

    [Fact]
    public async Task PublishAsync_AssistantMessage_IsSuppressed_WhenDeltaCommitMode()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(
            events,
            coreOptions: new CoreOptions(CommitAssistantTextDeltas: true),
            publishOptions: new AcpPublishOptions());

        await publisher.PublishAsync(new AssistantMessage("hello"), new FakeTurn(), new Dictionary<string, IAcpToolCall>(), CancellationToken.None);

        events.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_AssistantTextDelta_AlwaysPublishes()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        await publisher.PublishAsync(new AssistantTextDelta("d"), new FakeTurn(), new Dictionary<string, IAcpToolCall>(), CancellationToken.None);

        events.Updates.Should().ContainSingle()
            .Which.Should().BeOfType<AgentMessageChunk>()
            .Which.Content.As<TextContent>().Text.Should().Be("d");
    }

    [Fact]
    public async Task PublishAsync_Reasoning_IsSuppressed_WhenPublishReasoningFalse()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(
            events,
            coreOptions: new CoreOptions(),
            publishOptions: new AcpPublishOptions(PublishReasoning: false));

        await publisher.PublishAsync(new ReasoningMessage("think"), new FakeTurn(), new Dictionary<string, IAcpToolCall>(), CancellationToken.None);

        events.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_ReasoningMessage_Publishes_WhenEnabled_AndNotDeltaCommitMode()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(
            events,
            coreOptions: new CoreOptions(CommitReasoningTextDeltas: false),
            publishOptions: new AcpPublishOptions(PublishReasoning: true));

        await publisher.PublishAsync(new ReasoningMessage("r"), new FakeTurn(), new Dictionary<string, IAcpToolCall>(), CancellationToken.None);

        events.Updates.Should().ContainSingle()
            .Which.Should().BeOfType<AgentThoughtChunk>()
            .Which.Content.As<TextContent>().Text.Should().Be("r");
    }

    [Fact]
    public async Task PublishAsync_ReasoningTextDelta_Publishes_WhenEnabled()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(
            events,
            coreOptions: new CoreOptions(),
            publishOptions: new AcpPublishOptions(PublishReasoning: true));

        await publisher.PublishAsync(new ReasoningTextDelta("rd"), new FakeTurn(), new Dictionary<string, IAcpToolCall>(), CancellationToken.None);

        events.Updates.Should().ContainSingle()
            .Which.Should().BeOfType<AgentThoughtChunk>()
            .Which.Content.As<TextContent>().Text.Should().Be("rd");
    }

    [Fact]
    public async Task PublishAsync_ToolCallUpdate_NonString_UsesRawJsonText()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallRequested("1", "read_text_file", JsonSerializer.SerializeToElement(new { path = "demo.txt" })), turn, toolCalls, CancellationToken.None);
        await publisher.PublishAsync(new ToolCallInProgress("1"), turn, toolCalls, CancellationToken.None);

        var content = JsonSerializer.SerializeToElement(new { foo = "bar" });
        await publisher.PublishAsync(new ToolCallUpdate("1", content), turn, toolCalls, CancellationToken.None);

        var call = turn.ToolCalls.Started["1"];
        call.AddedContents.Should().HaveCount(1);

        var text = call.AddedContents.Single().As<ToolCallContentContent>().Content.As<TextContent>().Text;
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("foo").GetString().Should().Be("bar");
    }

    [Fact]
    public async Task PublishAsync_ToolCallUpdate_String_UsesStringValue()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallRequested("1", "read_text_file", JsonSerializer.SerializeToElement(new { path = "demo.txt" })), turn, toolCalls, CancellationToken.None);
        await publisher.PublishAsync(new ToolCallUpdate("1", JsonSerializer.SerializeToElement("hi")), turn, toolCalls, CancellationToken.None);

        var call = turn.ToolCalls.Started["1"];
        call.AddedContents.Single().As<ToolCallContentContent>().Content.As<TextContent>().Text.Should().Be("hi");
    }

    [Fact]
    public async Task PublishAsync_ToolCallRejected_WithDetails_MapsToFailedToolCall_AndRemoves()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallRejected("1", "missing_report_intent", ImmutableArray.Create("must_call:report_intent")), turn, toolCalls, CancellationToken.None);

        var call = turn.ToolCalls.Started["1"];
        call.Title.Should().Be("rejected");
        call.FailedMessages.Single().Should().Be("missing_report_intent: must_call:report_intent");

        toolCalls.Should().NotContainKey("1");
    }

    [Fact]
    public async Task PublishAsync_ToolCallCompleted_RemovesFromMap_AndSecondCompletionIsNoOp()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallRequested("1", "read_text_file", JsonSerializer.SerializeToElement(new { path = "demo.txt" })), turn, toolCalls, CancellationToken.None);
        await publisher.PublishAsync(new ToolCallCompleted("1", JsonSerializer.SerializeToElement(new { ok = true })), turn, toolCalls, CancellationToken.None);

        toolCalls.Should().NotContainKey("1");
        var call = turn.ToolCalls.Started["1"];
        call.CompletedCalls.Should().Be(1);

        // Duplicate completion should not throw and should not increase counts.
        await publisher.PublishAsync(new ToolCallCompleted("1", JsonSerializer.SerializeToElement(new { ok = true })), turn, toolCalls, CancellationToken.None);
        call.CompletedCalls.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_ToolCallUpdate_WhenMissingCall_IsNoOp()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallUpdate("missing", JsonSerializer.SerializeToElement("x")), turn, toolCalls, CancellationToken.None);

        turn.ToolCalls.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_ToolCallInProgress_WhenMissingCall_IsNoOp()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallInProgress("missing"), turn, toolCalls, CancellationToken.None);

        turn.ToolCalls.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_ToolCallFailed_InvokesFailedAndRemoves()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallRequested("1", "read_text_file", JsonSerializer.SerializeToElement(new { path = "demo.txt" })), turn, toolCalls, CancellationToken.None);
        await publisher.PublishAsync(new ToolCallFailed("1", "boom"), turn, toolCalls, CancellationToken.None);

        toolCalls.Should().NotContainKey("1");
        turn.ToolCalls.Started["1"].FailedMessages.Single().Should().Be("boom");
    }

    [Fact]
    public async Task PublishAsync_ToolCallCancelled_InvokesCancelledAndRemoves()
    {
        var events = new CapturingSessionEvents();
        var publisher = new AcpCommittedEventPublisher(events, new CoreOptions(), new AcpPublishOptions());

        var turn = new FakeTurn();
        var toolCalls = new Dictionary<string, IAcpToolCall>();

        await publisher.PublishAsync(new ToolCallRequested("1", "read_text_file", JsonSerializer.SerializeToElement(new { path = "demo.txt" })), turn, toolCalls, CancellationToken.None);
        await publisher.PublishAsync(new ToolCallCancelled("1"), turn, toolCalls, CancellationToken.None);

        toolCalls.Should().NotContainKey("1");
        turn.ToolCalls.Started["1"].CancelledCalls.Should().Be(1);
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

        public IReadOnlyCollection<string> ActiveToolCallIds => Array.Empty<string>();

        public IAcpToolCall Start(string toolCallId, string title, ToolKind kind)
        {
            var call = new FakeToolCall(toolCallId, title, kind);
            Started[toolCallId] = call;
            return call;
        }

        public Task CancelAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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

        public int CancelledCalls { get; private set; }

        public List<string> FailedMessages { get; } = new();
        public List<ToolCallContent> AddedContents { get; } = new();

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
            return Task.CompletedTask;
        }

        public Task FailedAsync(string message, CancellationToken cancellationToken = default)
        {
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
