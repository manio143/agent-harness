using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Harness.Llm.CommandSuggestions;

public sealed partial class QuickWorkCommandIntentSuggester
{
    // Keep the catalog small to fit common local-model context windows (e.g. 2k tokens).
    // Rough estimate: ~4 chars/token.
    private const int MaxCatalogChars = 6_000;

    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async Task<string?> BuildCommandCatalogAsync(
        string intent,
        ImmutableArray<ToolDefinition> offeredTools,
        CancellationToken cancellationToken)
    {
        var mcp = BuildMcpCmdletInfos(offeredTools)
            .Select(x => new CatalogItem(x.Name, x.Synopsis, Source: "mcp"))
            .ToImmutableArray();

        // Builtin PowerShell cmdlets (best-effort). This allows suggestions even when no MCP tools are offered.
        ImmutableArray<CatalogItem> builtin;
        try
        {
            var cmdlets = await _psCatalog.GetCmdletsAsync(cancellationToken).ConfigureAwait(false);
            builtin = cmdlets
                .Where(c => ShouldIncludeBuiltin(c.Name))
                .Select(c => new CatalogItem(c.Name, c.Synopsis ?? "", Source: "builtin"))
                .ToImmutableArray();
        }
        catch
        {
            builtin = ImmutableArray<CatalogItem>.Empty;
        }

        var all = mcp.AddRange(builtin);

        // Even if we have no catalog items, return an empty catalog (not null).
        // This keeps behavior deterministic and makes the caller unit-testable.
        if (all.Length == 0)
            return "";

        var intentTokens = Tokenize(intent);
        var ranked = all
            .Select(i => (Item: i, Score: Score(i, intentTokens)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .ToList();

        var sbCatalog = new StringBuilder();
        foreach (var i in ranked)
        {
            AppendItem(sbCatalog, i);
            if (sbCatalog.Length >= MaxCatalogChars)
                break;
        }

        return sbCatalog.ToString();
    }

    private static void AppendItem(StringBuilder sb, CatalogItem item)
    {
        // Keep synopsis single-line and try to avoid dumping command syntax/parameters.
        var synopsis = (item.Synopsis ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        synopsis = SanitizeSynopsis(item.Name, synopsis);

        if (synopsis.Length > 160)
            synopsis = synopsis[..160].TrimEnd() + "…";

        sb.Append("- ").Append(item.Name);
        if (!string.IsNullOrWhiteSpace(synopsis))
            sb.Append(" — ").Append(synopsis);
        sb.Append('\n');
    }

    private static string SanitizeSynopsis(string name, string synopsis)
    {
        if (string.IsNullOrWhiteSpace(synopsis))
            return "";

        // Heuristics: when Get-Help returns syntax/signature lines, they tend to contain lots of []/<>
        // and sometimes start with the cmdlet name.
        var looksLikeSyntax = synopsis.Contains("<CommonParameters>", StringComparison.OrdinalIgnoreCase)
                              || synopsis.Contains("[[", StringComparison.Ordinal)
                              || synopsis.Contains("[-", StringComparison.Ordinal)
                              || synopsis.Contains('<')
                              || synopsis.Contains(']');

        if (looksLikeSyntax && synopsis.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return "";

        return synopsis;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var norm = NonWord.Replace(text.ToLowerInvariant(), " ");
        var parts = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts
            .Where(p => p.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int Score(CatalogItem item, HashSet<string> intentTokens)
    {
        if (intentTokens.Count == 0) return 0;

        var text = (item.Name + " " + (item.Synopsis ?? ""));
        var tokens = Tokenize(text);

        var score = 0;
        foreach (var t in tokens)
            if (intentTokens.Contains(t)) score++;

        return score;
    }

    private static bool ShouldIncludeBuiltin(string name)
    {
        // Keep the catalog focused on common local scripting workflows.
        // Filter out remote/runspace/session plumbing that tends to confuse small models.
        if (name.Contains("PSSession", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("Runspace", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("CimSession", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("EventSubscriber", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("Debug-", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private readonly record struct CatalogItem(string Name, string Synopsis, string Source);
}
