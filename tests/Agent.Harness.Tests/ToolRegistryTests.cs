using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void Ctor_DuplicateToolNames_Throws()
    {
        var h1 = new FakeHandler("x");
        var h2 = new FakeHandler("x");

        var act = () => new ToolRegistry(new IToolHandler[] { h1, h2 });

        act.Should().Throw<InvalidOperationException>().WithMessage("duplicate_tool_handler:x");
    }

    [Fact]
    public void Definitions_AreSortedAndMatchOfferedToolNames()
    {
        var reg = new ToolRegistry(new IToolHandler[]
        {
            new FakeHandler("b"),
            new FakeHandler("a"),
        });

        reg.Definitions.Select(d => d.Name).Should().Equal("a", "b");
        reg.OfferedToolNames.Should().BeEquivalentTo(new[] { "a", "b" });
    }

    private sealed class FakeHandler(string name) : IToolHandler
    {
        public ToolDefinition Definition { get; } = new(name, "", JsonDocument.Parse("{}").RootElement);

        public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
    }
}
