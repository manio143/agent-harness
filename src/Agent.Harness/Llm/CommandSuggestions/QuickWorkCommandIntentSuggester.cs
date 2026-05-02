using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Shell.Mcp;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Llm.CommandSuggestions;

public sealed partial class QuickWorkCommandIntentSuggester : ICommandIntentSuggester
{
    private readonly IChatClient _chat;
    private readonly IPowerShellCommandCatalog _psCatalog;

    public QuickWorkCommandIntentSuggester(IChatClient chat, IPowerShellCommandCatalog psCatalog)
    {
        _chat = chat;
        _psCatalog = psCatalog;
    }

    public async Task<ImmutableArray<CommandSuggestion>> SuggestAsync(
        string intent,
        ImmutableArray<ToolDefinition> offeredTools,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return ImmutableArray<CommandSuggestion>.Empty;

        var prompt = await BuildPromptAsync(intent, offeredTools, cancellationToken).ConfigureAwait(false);
        if (prompt is null)
            return ImmutableArray<CommandSuggestion>.Empty;

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

    private static IEnumerable<(string Name, string Synopsis)> BuildMcpCmdletInfos(ImmutableArray<ToolDefinition> offeredTools)
    {
        var verbs = McpApprovedVerbs.CreateDefault();

        foreach (var t in offeredTools)
        {
            var idx = t.Name.IndexOf("__", StringComparison.Ordinal);
            if (idx <= 0 || idx >= t.Name.Length - 2) continue;

            var tool = t.Name[(idx + 2)..];
            var mapping = McpCmdletNameMapper.Map(tool, verbs);
            yield return (mapping.CmdletName, t.Description ?? "");
        }
    }
}
