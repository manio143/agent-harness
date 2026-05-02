using System;
using System.Collections;
using System.Collections.Immutable;
using System.IO;

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
    private readonly string? _debugPath;

    public CommandIntentPsContext(
        ICommandIntentSuggester suggester,
        Func<ImmutableArray<ToolDefinition>> getOfferedTools,
        bool enabled,
        string? debugPath = null)
    {
        _suggester = suggester;
        _getOfferedTools = getOfferedTools;
        _enabled = enabled;
        _debugPath = debugPath;
    }

    public Hashtable[] Suggest(string intent)
    {
        void Debug(string msg)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_debugPath))
                    return;

                File.AppendAllText(_debugPath!, $"{DateTimeOffset.UtcNow:O} {msg}\n");
            }
            catch
            {
                // ignore
            }
        }

        if (!_enabled)
        {
            Debug("disabled");
            return Array.Empty<Hashtable>();
        }

        if (string.IsNullOrWhiteSpace(intent))
        {
            Debug("empty_intent");
            return Array.Empty<Hashtable>();
        }

        Debug($"begin intent={intent}");
        var tools = _getOfferedTools();
        Debug($"offered_tools count={tools.Length}");

        ImmutableArray<CommandSuggestion> suggestions;
        try
        {
            Debug("calling_suggester");
            suggestions = _suggester.SuggestAsync(intent, tools, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Debug($"suggester_returned count={suggestions.Length}");
        }
        catch (Exception e)
        {
            Debug($"suggester_threw {e.GetType().Name}: {e.Message}");
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
