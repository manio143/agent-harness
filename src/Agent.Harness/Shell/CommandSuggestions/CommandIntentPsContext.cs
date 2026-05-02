using System.Collections;
using System.Collections.Immutable;

namespace Agent.Harness.Shell.CommandSuggestions;

using Agent.Harness.Llm.CommandSuggestions;

/// <summary>
/// PowerShell-bridging context for intent-based command suggestions.
///
/// Intended usage from PowerShell:
///   $global:__cmdSuggestCtx.Suggest("...")
///
/// Output is shaped for easy consumption in PowerShell (array of hashtables).
/// </summary>
public sealed class CommandIntentPsContext
{
    private readonly ICommandIntentSuggester _suggester;
    private readonly Func<ImmutableArray<ToolDefinition>> _getOfferedTools;
    private readonly bool _enabled;
    public CommandIntentPsContext(
        ICommandIntentSuggester suggester,
        Func<ImmutableArray<ToolDefinition>> getOfferedTools,
        bool enabled)
    {
        _suggester = suggester;
        _getOfferedTools = getOfferedTools;
        _enabled = enabled;
    }

    public Hashtable[] Suggest(string intent)
    {
        if (!_enabled)
            return Array.Empty<Hashtable>();

        if (string.IsNullOrWhiteSpace(intent))
            return Array.Empty<Hashtable>();

        var tools = _getOfferedTools();

        ImmutableArray<CommandSuggestion> suggestions;
        try
        {
            suggestions = _suggester.SuggestAsync(intent, tools, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            suggestions = ImmutableArray<CommandSuggestion>.Empty;
        }

        // Shaped as hashtables for natural PowerShell formatting.
        return suggestions
            .Select(s => new Hashtable
            {
                { "name", s.Name },
                { "reason", s.Reason },
            })
            .ToArray();
    }
}
