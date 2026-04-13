using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ClientToolCatalogMergeTests
{
    [Fact]
    public void Merge_PreservesExistingTools_AndAddsBuiltinsWithoutDuplicates()
    {
        var existing = ImmutableArray.Create(new ToolDefinition(
            "fake_server__echo",
            "echo",
            ToolSchemas.ReadTextFile.InputSchema));

        var builtins = ClientToolCatalog.BuildBuiltins(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true },
            Terminal = true,
        });

        var merged = ClientToolCatalog.Merge(existing, builtins);

        merged.Select(t => t.Name).Should().Contain(new[]
        {
            "fake_server__echo",
            "read_text_file",
            "execute_command",
        });

        merged.Select(t => t.Name).Distinct().Count().Should().Be(merged.Length);
    }
}
