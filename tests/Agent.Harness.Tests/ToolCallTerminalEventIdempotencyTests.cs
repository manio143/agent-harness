using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ToolCallTerminalEventIdempotencyTests
{
    [Fact]
    public void ObservedToolCallCompleted_WhenAlreadyTerminal_IsIgnored()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new ToolCallCompleted("call_1", JsonSerializer.SerializeToElement(new { ok = true }))),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(new ToolDefinition("read_text_file", "", JsonSerializer.SerializeToElement(new { type = "object" }))));

        var result = Core.Reduce(state, new ObservedToolCallCompleted("call_1", new { ok = true }));

        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void ObservedToolCallFailed_WhenAlreadyTerminal_IsIgnored()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new ToolCallFailed("call_1", "boom")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(new ToolDefinition("read_text_file", "", JsonSerializer.SerializeToElement(new { type = "object" }))));

        var result = Core.Reduce(state, new ObservedToolCallFailed("call_1", "boom"));

        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void ObservedToolCallCancelled_WhenAlreadyTerminal_IsIgnored()
    {
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new ToolCallCancelled("call_1")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(new ToolDefinition("read_text_file", "", JsonSerializer.SerializeToElement(new { type = "object" }))));

        var result = Core.Reduce(state, new ObservedToolCallCancelled("call_1"));

        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }
}
