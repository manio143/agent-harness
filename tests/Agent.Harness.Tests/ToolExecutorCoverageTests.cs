using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Tools;
using Agent.Harness.Tools.Executors;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolExecutorCoverageTests
{
    private sealed class AlwaysExecutor(string name) : IToolCallExecutor
    {
        public bool CanExecute(string toolName) => toolName == name;

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }

    [Fact]
    public void ToolArgs_Normalize_WhenJsonElementObject_ReturnsCaseInsensitiveDictionary()
    {
        var je = JsonDocument.Parse("{\"Path\":\"x\"}").RootElement.Clone();
        var dict = ToolArgs.Normalize(je);

        dict.Should().ContainKey("path");
        dict["path"].GetString().Should().Be("x");
    }

    [Fact]
    public void ToolArgs_Normalize_WhenNotObject_ReturnsEmpty()
    {
        var je = JsonDocument.Parse("[1,2]").RootElement.Clone();
        var dict = ToolArgs.Normalize(je);

        dict.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallRouter_WhenUnknownTool_ReturnsObservedToolCallFailed_unknown_tool()
    {
        var router = new ToolCallRouter(Array.Empty<IToolCallExecutor>());

        var obs = await router.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "nope", new { }),
            CancellationToken.None);

        obs.Should().HaveCount(1);
        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("unknown_tool");
    }

    [Fact]
    public async Task ToolCallRouter_WhenExecutorMatches_UsesThatExecutor()
    {
        var router = new ToolCallRouter(new IToolCallExecutor[] { new AlwaysExecutor("x") });

        var obs = await router.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "x", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single().ToolId.Should().Be("t1");
    }

    [Fact]
    public async Task ToolCallRouter_WhenFirstDoesNotMatch_UsesLaterExecutor()
    {
        var router = new ToolCallRouter(new IToolCallExecutor[] { new AlwaysExecutor("no"), new AlwaysExecutor("yes") });

        var obs = await router.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "yes", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single().ToolId.Should().Be("t1");
    }
}
