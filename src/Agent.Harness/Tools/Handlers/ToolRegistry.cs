using System.Collections.Immutable;

namespace Agent.Harness.Tools.Handlers;

public sealed class ToolRegistry
{
    private readonly ImmutableDictionary<string, IToolHandler> _byName;

    public ToolRegistry(IEnumerable<IToolHandler> handlers)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, IToolHandler>(StringComparer.Ordinal);

        foreach (var h in handlers)
        {
            var name = h.Definition.Name;
            if (builder.ContainsKey(name))
                throw new InvalidOperationException($"duplicate_tool_handler:{name}");

            builder.Add(name, h);
        }

        _byName = builder.ToImmutable();

        Definitions = _byName.Values
            .Select(h => h.Definition)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        OfferedToolNames = Definitions
            .Select(d => d.Name)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    public ImmutableArray<ToolDefinition> Definitions { get; }

    public ImmutableHashSet<string> OfferedToolNames { get; }

    public bool CanExecute(string toolName) => _byName.ContainsKey(toolName);

    public IToolHandler GetRequired(string toolName)
        => _byName.TryGetValue(toolName, out var h)
            ? h
            : throw new InvalidOperationException($"tool_not_registered:{toolName}");
}
