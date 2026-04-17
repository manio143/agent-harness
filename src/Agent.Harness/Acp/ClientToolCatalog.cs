using System.Collections.Immutable;
using Agent.Acp.Schema;

namespace Agent.Harness.Acp;

public static class ClientToolCatalog
{
    public static ImmutableArray<ToolDefinition> BuildBuiltins(ClientCapabilities caps)
    {
        var b = ImmutableArray.CreateBuilder<ToolDefinition>();

        // fs
        if (caps.Fs?.ReadTextFile == true)
            b.Add(ToolSchemas.ReadTextFile);

        if (caps.Fs?.WriteTextFile == true)
            b.Add(ToolSchemas.WriteTextFile);

        // fs patch (requires both read + write)
        if (caps.Fs?.ReadTextFile == true && caps.Fs?.WriteTextFile == true)
            b.Add(ToolSchemas.PatchTextFile);

        // terminal
        if (caps.Terminal)
            b.Add(ToolSchemas.ExecuteCommand);

        return b.ToImmutable();
    }

    public static ImmutableArray<ToolDefinition> Merge(ImmutableArray<ToolDefinition> existing, ImmutableArray<ToolDefinition> add)
    {
        if (existing.IsDefaultOrEmpty)
            return add;
        if (add.IsDefaultOrEmpty)
            return existing;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var b = ImmutableArray.CreateBuilder<ToolDefinition>(existing.Length + add.Length);

        foreach (var t in existing)
        {
            if (seen.Add(t.Name))
                b.Add(t);
        }

        foreach (var t in add)
        {
            if (seen.Add(t.Name))
                b.Add(t);
        }

        return b.ToImmutable();
    }
}
