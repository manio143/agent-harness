using System.Collections.Immutable;

namespace Agent.Harness.Shell.Mcp;

public static class McpApprovedVerbs
{
    public static ImmutableHashSet<string> CreateDefault()
    {
        // PowerShell approved verbs (subset) + pragmatic extensions.
        // NOTE: Keep deterministic and stable across environments.
        // Intentionally smaller than PowerShell's full approved verb set.
        // We only include verbs we want to infer from MCP tool names.
        // This avoids awkward mappings like advanced_copy -> Copy-Advanced.
        var verbs = new[]
        {
            "Get",
            "Set",
            "New",
            "Remove",
            "Add",
            "Clear",
            "Find",
            "Search",
            "Test",
            "Update",
            "List", // pragmatic extension
        };

        return verbs.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
