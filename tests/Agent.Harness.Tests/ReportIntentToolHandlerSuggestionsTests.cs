using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Llm.CommandSuggestions;
using Agent.Harness.Tools.Handlers;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ReportIntentToolHandlerSuggestionsTests
{
    [Fact]
    public async Task ReportIntent_IncludesSuggestedCommands()
    {
        var suggester = new FakeSuggester();
        var h = new ReportIntentToolHandler(threadTools: null, threadId: "t1", suggester: suggester);

        var toolCall = new ExecuteToolCall(
            ToolName: "report_intent",
            ToolId: "call_0",
            Args: JsonDocument.Parse("{\"intent\":\"list work items\"}").RootElement);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.ReportIntent) };

        var events = await h.ExecuteAsync(state, toolCall, CancellationToken.None);
        events.Should().ContainSingle();

        var completed = events.OfType<ObservedToolCallCompleted>().Single();
        completed.ToolId.Should().Be("call_0");

        var json = (JsonElement)completed.Result;
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("suggestedCommands")[0].GetProperty("name").GetString().Should().Be("Get-WorkItems");
    }

    private sealed class FakeSuggester : ICommandIntentSuggester
    {
        public Task<ImmutableArray<CommandSuggestion>> SuggestAsync(string intent, ImmutableArray<ToolDefinition> offeredTools, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create(new CommandSuggestion("Get-WorkItems", "Because")));
    }
}
