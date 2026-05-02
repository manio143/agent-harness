using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Harness.Llm.CommandSuggestions;

public sealed partial class QuickWorkCommandIntentSuggester
{
    // Rough budget: 4k tokens ~ 16k chars for the catalog portion.
    private const int MaxCatalogChars = 16_000;

    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async Task<string?> BuildPromptAsync(
        string intent,
        ImmutableArray<ToolDefinition> offeredTools,
        CancellationToken cancellationToken)
    {
        var mcp = BuildMcpCmdletInfos(offeredTools)
            .Select(x => new CatalogItem(x.Name, x.Synopsis, Source: "mcp"))
            .ToImmutableArray();

        var ps = await _psCatalog.GetCmdletsAsync(cancellationToken).ConfigureAwait(false);
        var psItems = ps
            .Select(x => new CatalogItem(x.Name, x.Synopsis, Source: "powershell"))
            .ToImmutableArray();

        // Smart truncation: include all MCP tools (usually small), then pick PowerShell builtins by keyword overlap.
        var intentTokens = Tokenize(intent);

        var scoredPs = psItems
            .Select(i => (i, score: Score(i, intentTokens)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.i)
            .ToImmutableArray();

        var selected = ImmutableArray.CreateBuilder<CatalogItem>();
        selected.AddRange(mcp);

        var sbCatalog = new StringBuilder();
        foreach (var i in selected)
            AppendItem(sbCatalog, i);

        foreach (var i in scoredPs)
        {
            if (sbCatalog.Length >= MaxCatalogChars)
                break;

            AppendItem(sbCatalog, i);
        }

        var prompt = "You are selecting PowerShell commands relevant to an intent.\n" +
                     "Return STRICT JSON only: an array of objects with properties name (string) and reason (string).\n" +
                     "Return at most 8 items. Use only commands from the provided list.\n\n" +
                     $"Intent: {intent}\n\n" +
                     "Commands (name — synopsis):\n" +
                     sbCatalog +
                     "\nJSON:";

        return prompt;
    }

    private static void AppendItem(StringBuilder sb, CatalogItem item)
    {
        // Keep synopsis single-line.
        var synopsis = (item.Synopsis ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (synopsis.Length > 160)
            synopsis = synopsis[..160].TrimEnd() + "…";

        sb.Append("- ").Append(item.Name);
        if (!string.IsNullOrWhiteSpace(synopsis))
            sb.Append(" — ").Append(synopsis);
        sb.Append('\n');
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

    private readonly record struct CatalogItem(string Name, string Synopsis, string Source);
}
