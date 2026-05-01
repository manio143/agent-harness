using System.Collections.Immutable;
using System.Text;

namespace Agent.Harness.Shell.Mcp;

public static class McpCmdletNameMapper
{
    public sealed record Mapping(string Verb, string Noun, string CmdletName);

    public static Mapping Map(string toolNameSnakeCase, ImmutableHashSet<string> approvedVerbs)
    {
        if (string.IsNullOrWhiteSpace(toolNameSnakeCase))
            throw new ArgumentException("tool_name_required", nameof(toolNameSnakeCase));

        var parts = toolNameSnakeCase
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            throw new ArgumentException("tool_name_required", nameof(toolNameSnakeCase));

        var first = ToVerbCandidate(parts[0]);
        var last = ToVerbCandidate(parts[^1]);

        string verb;
        string[] nounParts;

        // Prefer first when both are verbs.
        if (approvedVerbs.Contains(first))
        {
            verb = first;
            nounParts = parts.Skip(1).ToArray();
        }
        else if (approvedVerbs.Contains(last))
        {
            verb = last;
            nounParts = parts.Take(parts.Length - 1).ToArray();
        }
        else
        {
            verb = "Invoke";
            nounParts = parts;
        }

        var noun = ToPascal(nounParts);
        if (string.IsNullOrWhiteSpace(noun))
            noun = "Tool";

        return new Mapping(Verb: verb, Noun: noun, CmdletName: $"{verb}-{noun}");
    }

    private static string ToVerbCandidate(string s)
        => string.IsNullOrWhiteSpace(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string ToPascal(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1)
                sb.Append(p[1..]);
        }
        return sb.ToString();
    }
}
