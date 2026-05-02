using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Agent.Harness.Llm.CommandSuggestions;

public static class PowerShellBuiltinCatalog
{
    public sealed record CmdletInfo(string Name, string Synopsis);

    private static readonly object Gate = new();
    private static ImmutableArray<CmdletInfo>? _cached;

    public static ImmutableArray<CmdletInfo> GetDefaultCmdlets()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached.Value;

            using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();

            // NOTE: We do command discovery and synopsis extraction in PowerShell in one runspace.
            // This method is called only via PowerShellCommandCatalog (Task.Run) so we avoid deadlocks
            // when suggestions are invoked from within the shell.
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("""
$allowed = @('Microsoft.PowerShell.Management','Microsoft.PowerShell.Utility')

$cmds = Get-Command -CommandType Cmdlet |
  Where-Object { $_.ModuleName -and ($allowed -contains $_.ModuleName) } |
  Sort-Object -Property Name |
  Select-Object -First 200

$cmds | ForEach-Object {
  $h = Get-Help -Name $_.Name -ErrorAction SilentlyContinue
  $syn = ''
  # Prefer Description (avoids parameter/signature dumps sometimes found in Synopsis)
  if ($null -ne $h -and $null -ne $h.Description) {
    $d = $h.Description | Select-Object -First 1
    if ($null -ne $d -and $null -ne $d.Text) {
      $syn = (($d.Text | Where-Object { $_ }) -join ' ')
    }
  }
  if ([string]::IsNullOrWhiteSpace($syn) -and $null -ne $h -and $null -ne $h.Synopsis) {
    $syn = ($h.Synopsis | Select-Object -First 1)
  }
  [pscustomobject]@{ name = $_.Name; synopsis = $syn }
}
""");

            var results = ps.Invoke();

            var items = results
                .Select(o =>
                {
                    var name = o.Properties["name"]?.Value?.ToString() ?? "";
                    var synopsis = o.Properties["synopsis"]?.Value?.ToString() ?? "";
                    return new CmdletInfo(name.Trim(), synopsis.Trim());
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

            _cached = items;
            return items;
        }
    }
}

