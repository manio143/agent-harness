using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Shell.Mcp;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Llm.CommandSuggestions;

public sealed class QuickWorkCommandIntentSuggester : ICommandIntentSuggester
{
    private readonly IChatClient _chat;

    public QuickWorkCommandIntentSuggester(IChatClient chat)
    {
        _chat = chat;
    }

    public async Task<ImmutableArray<CommandSuggestion>> SuggestAsync(
        string intent,
        ImmutableArray<ToolDefinition> offeredTools,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return ImmutableArray<CommandSuggestion>.Empty;

        // Build a lean command catalog: MCP proxy cmdlets + core PowerShell cmdlets.
        var mcpCmdlets = BuildMcpCmdletNames(offeredTools);
        var psCmdlets = PowerShellBuiltinCatalog.GetDefaultCmdlets();

        // Keep the prompt small. This is a nudge, not an exhaustive planner.
        var catalog = mcpCmdlets.Concat(psCmdlets).Distinct(StringComparer.OrdinalIgnoreCase).Take(250).ToArray();

        var prompt = "You are selecting PowerShell commands relevant to an intent.\n" +
                     "Return STRICT JSON: an array of objects with properties name (string) and reason (string).\n" +
                     "Return at most 8 items. Use only commands from the provided list.\n\n" +
                     $"Intent: {intent}\n\n" +
                     "Commands:\n" + string.Join("\n", catalog.Select(x => "- " + x)) +
                     "\n\nJSON:";

        var resp = await _chat.GetResponseAsync(
            new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt) },
            options: new ChatOptions { Temperature = 0 },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = string.Join("\n", resp.Messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text));
        if (string.IsNullOrWhiteSpace(text))
            return ImmutableArray<CommandSuggestion>.Empty;

        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ImmutableArray<CommandSuggestion>.Empty;

            var b = ImmutableArray.CreateBuilder<CommandSuggestion>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!el.TryGetProperty("reason", out var r) || r.ValueKind != JsonValueKind.String) continue;

                var name = n.GetString() ?? "";
                var reason = r.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                b.Add(new CommandSuggestion(name.Trim(), reason.Trim()));
                if (b.Count >= 8) break;
            }

            return b.ToImmutable();
        }
        catch
        {
            return ImmutableArray<CommandSuggestion>.Empty;
        }
    }

    private static IEnumerable<string> BuildMcpCmdletNames(ImmutableArray<ToolDefinition> offeredTools)
    {
        var verbs = McpApprovedVerbs.CreateDefault();

        foreach (var t in offeredTools)
        {
            var idx = t.Name.IndexOf("__", StringComparison.Ordinal);
            if (idx <= 0 || idx >= t.Name.Length - 2) continue;

            var tool = t.Name[(idx + 2)..];
            var mapping = McpCmdletNameMapper.Map(tool, verbs);
            yield return mapping.CmdletName;
        }
    }
}
