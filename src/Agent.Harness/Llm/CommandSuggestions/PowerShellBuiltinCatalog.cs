using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Agent.Harness.Llm.CommandSuggestions;

public static class PowerShellBuiltinCatalog
{
    private static readonly object Gate = new();
    private static ImmutableArray<string>? _cached;

    public static ImmutableArray<string> GetDefaultCmdlets()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached.Value;

            // Lean filter: core modules only.
            var allowedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Microsoft.PowerShell.Management",
                "Microsoft.PowerShell.Utility",
            };

            using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();

            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Get-Command").AddParameter("CommandType", new[] { "Cmdlet" });
            var results = ps.Invoke<CommandInfo>();

            var names = results
                .Where(c => c.ModuleName is not null && allowedModules.Contains(c.ModuleName))
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

            _cached = names;
            return names;
        }
    }
}
