using System.Collections.Immutable;

namespace Agent.Harness.Llm.CommandSuggestions;

public interface ICommandIntentSuggester
{
    Task<ImmutableArray<CommandSuggestion>> SuggestAsync(string intent, ImmutableArray<ToolDefinition> offeredTools, CancellationToken cancellationToken);
}

public sealed record CommandSuggestion(string Name, string Reason);
