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

        // terminal
        if (caps.Terminal)
            b.Add(ToolSchemas.ExecuteCommand);

        return b.ToImmutable();
    }
}
