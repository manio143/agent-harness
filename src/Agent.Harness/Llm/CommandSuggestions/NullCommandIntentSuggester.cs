using System.Collections.Immutable;

namespace Agent.Harness.Llm.CommandSuggestions;

public sealed class NullCommandIntentSuggester : ICommandIntentSuggester
{
    public static readonly NullCommandIntentSuggester Instance = new();

    private NullCommandIntentSuggester() { }

    public Task<ImmutableArray<CommandSuggestion>> SuggestAsync(string intent, ImmutableArray<ToolDefinition> offeredTools, CancellationToken cancellationToken)
        => Task.FromResult(ImmutableArray<CommandSuggestion>.Empty);
}
