using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Agent.Harness.Shell.Mcp;

namespace Agent.Harness.Llm.CommandSuggestions;

/// <summary>
/// Deterministic, no-LLM intent suggester.
///
/// Uses simple keyword overlap against a small catalog (MCP proxy cmdlets + core PowerShell cmdlets).
/// Intended primarily for deterministic samples and fast/offline runs.
/// </summary>
public sealed class HeuristicCommandIntentSuggester : ICommandIntentSuggester
{
    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<ImmutableArray<CommandSuggestion>> SuggestAsync(
        string intent,
        ImmutableArray<ToolDefinition> offeredTools,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return Task.FromResult(ImmutableArray<CommandSuggestion>.Empty);

        var intentTokens = Tokenize(intent);
        if (intentTokens.Count == 0)
            return Task.FromResult(ImmutableArray<CommandSuggestion>.Empty);

        var catalog = BuildCatalog(offeredTools);

        var scored = catalog
            .Select(name => (name, score: Score(name, intentTokens)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(x => new CommandSuggestion(
                x.name,
                Reason: "keyword match"))
            .ToImmutableArray();

        return Task.FromResult(scored);
    }

    private static ImmutableArray<string> BuildCatalog(ImmutableArray<ToolDefinition> offeredTools)
    {
        var verbs = McpApprovedVerbs.CreateDefault();

        var mcpCmdlets = offeredTools
            .Select(t => t.Name)
            .Select(n =>
            {
                var idx = n.IndexOf("__", StringComparison.Ordinal);
                if (idx <= 0 || idx >= n.Length - 2) return null;
                var tool = n[(idx + 2)..];
                return McpCmdletNameMapper.Map(tool, verbs).CmdletName;
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>();

        var psCmdlets = PowerShellBuiltinCatalog.GetDefaultCmdlets();

        return mcpCmdlets
            .Concat(psCmdlets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToImmutableArray();
    }

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
        // Cmdlets are Verb-Noun. Split on '-' and score overlap.
        var tokens = Tokenize(cmdletName);
        var score = 0;
        foreach (var t in tokens)
            if (intentTokens.Contains(t)) score++;

        // small boost for substring match (e.g. "file" in "Get-ChildItem" won't match, but ok)
        var lowered = cmdletName.ToLowerInvariant();
        if (intentTokens.Any(t => lowered.Contains(t, StringComparison.OrdinalIgnoreCase)))
            score++;

        return score;
    }
}
