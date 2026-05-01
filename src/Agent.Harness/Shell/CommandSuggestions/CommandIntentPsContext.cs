using System.Collections.Generic;
using System.Collections.Immutable;

namespace Agent.Harness.Shell.CommandSuggestions;


/// <summary>
/// PowerShell-bridging context for intent-based command suggestions.
///
/// Intended usage from PowerShell:
///   $global:__cmdSuggestCtx.Suggest("...")
///
/// IMPORTANT: Avoid returning Hashtable from C# here. In practice it can cause PowerShell
/// to behave oddly (and we saw hangs in-shell). Return plain dictionaries instead.
/// </summary>
public sealed class CommandIntentPsContext
{
    private readonly ImmutableArray<string> _catalog;
    private readonly bool _enabled;

    public CommandIntentPsContext(
        ImmutableArray<string> catalog,
        bool enabled)
    {
        _catalog = catalog;
        _enabled = enabled;
    }

    public object[] Suggest(string intent)
    {
        if (!_enabled)
            return Array.Empty<object>();

        if (string.IsNullOrWhiteSpace(intent))
            return Array.Empty<object>();

        // Deterministic, no PowerShell re-entry, no LLM call.
        var intentTokens = Tokenize(intent);
        if (intentTokens.Count == 0)
            return Array.Empty<object>();

        var suggestions = _catalog
            .Select(name => (name, score: Score(name, intentTokens)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(x => (object)new Dictionary<string, string>
            {
                { "name", x.name },
                { "reason", "keyword match" },
            })
            .ToArray();

        return suggestions;
    }

    private static readonly System.Text.RegularExpressions.Regex NonWord = new("[^a-z0-9]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static HashSet<string> Tokenize(string text)
    {
        var norm = NonWord.Replace(text.ToLowerInvariant(), " ");
        var parts = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts
            .Where(p => p.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int Score(string cmdletName, HashSet<string> intentTokens)
    {
        var tokens = Tokenize(cmdletName);
        var score = 0;
        foreach (var t in tokens)
            if (intentTokens.Contains(t)) score++;

        var lowered = cmdletName.ToLowerInvariant();
        if (intentTokens.Any(t => lowered.Contains(t, StringComparison.OrdinalIgnoreCase)))
            score++;

        return score;
    }
}
