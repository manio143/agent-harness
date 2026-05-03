using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Agent.Harness.Shell.Mcp;
using Agent.Harness.Shell.CommandSuggestions;

namespace Agent.Harness.Shell;

/// <summary>
/// In-process PowerShell Core runspace used as a "virtual shell".
///
/// Notes / intent:
/// - Per-session (and per-thread) instance, so state (variables, functions) can persist.
/// - Best-effort filesystem containment via:
///   - ConstrainedLanguage
///   - A FileSystem PSDrive rooted at a session-specific directory (sandbox:)
///   - A hybrid project: drive (ACP content operations + local listing/navigation) when session cwd is known
///
/// This is a tool-surface restriction, not a perfect sandbox.
/// </summary>
public sealed class InProcessPowerShellSession : IDisposable
{
    private readonly object _gate = new();
    private readonly string _workingDir;
    private readonly Runspace _runspace;

    private readonly Agent.Harness.Acp.IMcpToolInvoker? _mcp;
    private readonly Agent.Harness.Llm.CommandSuggestions.ICommandIntentSuggester _commandIntentSuggester;
    private readonly bool _includeSuggestionsInShell;

    private sealed class OfferedToolsSnapshot
    {
        public ImmutableArray<Agent.Harness.ToolDefinition> Tools { get; }
        public OfferedToolsSnapshot(ImmutableArray<Agent.Harness.ToolDefinition> tools) => Tools = tools;
    }

    private OfferedToolsSnapshot _offeredTools = new(default);
    private string? _mcpSignature;
    private ImmutableHashSet<string> _mcpServers = ImmutableHashSet<string>.Empty;

    public InProcessPowerShellSession(
        string workingDir,
        Agent.Acp.Acp.IAcpClientCaller? client = null,
        string? sessionId = null,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null,
        Agent.Harness.Acp.IMcpToolInvoker? mcp = null,
        Agent.Harness.Llm.CommandSuggestions.ICommandIntentSuggester? commandIntentSuggester = null,
        bool includeSuggestionsInShell = true,
        System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition> offeredTools = default)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            throw new ArgumentException("workingDir is required", nameof(workingDir));

        _workingDir = workingDir;
        Directory.CreateDirectory(_workingDir);

        _mcp = mcp;
        _commandIntentSuggester = commandIntentSuggester ?? Agent.Harness.Llm.CommandSuggestions.NullCommandIntentSuggester.Instance;
        _includeSuggestionsInShell = includeSuggestionsInShell;
        System.Threading.Interlocked.Exchange(ref _offeredTools, new OfferedToolsSnapshot(offeredTools));

        var iss = InitialSessionState.CreateDefault2();

        // Binary cmdlet used by generated MCP proxy functions.
        iss.Commands.Add(new SessionStateCmdletEntry(
            name: "Invoke-McpTool",
            implementingType: typeof(InvokeMcpToolCmdlet),
            helpFileName: null));

        // Ensure basic built-in cmdlets are available (file ops, formatting, etc.).
        // In some hosting scenarios CreateDefault2 may not auto-import these.
        iss.ImportPSModule(new[]
        {
            "Microsoft.PowerShell.Management",
            "Microsoft.PowerShell.Utility",
        });

        var canCreateProjectDrive = !string.IsNullOrWhiteSpace(sessionCwd)
            || (!string.IsNullOrWhiteSpace(sessionId) && store is not null);
        if (canCreateProjectDrive)
        {
            iss.Providers.Add(new SessionStateProviderEntry(
                name: "ProjectDrive",
                implementingType: typeof(ProjectDriveContentProvider),
                helpFileName: null));
        }

        // Full language mode: required for richer PowerShell UX (dynamic modules/functions)
        // and for invoking non-core methods during MCP proxy cmdlet bridging.
        // This is still a best-effort containment mechanism, not a hardened sandbox.
        iss.LanguageMode = PSLanguageMode.FullLanguage;

        iss.StartupScripts.Add("$ErrorActionPreference = 'Stop'");


        _runspace = RunspaceFactory.CreateRunspace(iss);
        _runspace.Open();

        // Attach command-suggestion context + helper cmdlets.
        try
        {
            _runspace.SessionStateProxy.SetVariable(
                "__cmdSuggestCtx",
                new CommandIntentPsContext(
                    _commandIntentSuggester,
                    getOfferedTools: () => System.Threading.Volatile.Read(ref _offeredTools).Tools,
                    enabled: _includeSuggestionsInShell));

            using var psInit = PowerShell.Create();
            psInit.Runspace = _runspace;
            psInit.AddScript("""
function Find-AgentCommand {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory=$true)][string]$Intent
  )

  $global:__cmdSuggestCtx.Suggest($Intent)
}
""");
            psInit.Invoke();
        }
        catch
        {
            // ignore: shell still works without suggestion helpers
        }

        // Best-effort: expose a project: drive with ACP-backed content and local listing/navigation.
        if (canCreateProjectDrive)
        {
            try
            {
                _runspace.SessionStateProxy.SetVariable("__project_drive_ctx", new ProjectDrivePsContext(sessionId, client, sessionCwd, store));
                try { _runspace.SessionStateProxy.Drive.Remove("project", force: true, scope: "Global"); } catch { /* ignore */ }
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;
                ps.AddScript("New-PSDrive -Name project -PSProvider ProjectDrive -Root / -Scope Global | Out-Null");
                ps.Invoke();
            }
            catch
            {
                // ignore: the shell still works without project:
            }
        }

        // Best-effort: expose a sandbox: drive rooted at the session working dir.
        try
        {
            try { _runspace.SessionStateProxy.Drive.Remove("sandbox", force: true, scope: "Global"); } catch { /* ignore */ }

            var fs = _runspace.SessionStateProxy.Provider.Get("FileSystem").FirstOrDefault();
            if (fs is not null)
            {
                _runspace.SessionStateProxy.Drive.New(new PSDriveInfo(
                    name: "sandbox",
                    provider: fs,
                    root: _workingDir,
                    description: "Agent session sandbox drive",
                    credential: null), scope: "Global");
            }
        }
        catch
        {
            // ignore
        }

        RefreshMcpProxyModules();
    }

    public void UpdateOfferedTools(ImmutableArray<Agent.Harness.ToolDefinition> offeredTools)
    {
        System.Threading.Interlocked.Exchange(ref _offeredTools, new OfferedToolsSnapshot(offeredTools));
    }

    private void RefreshMcpProxyModules()
    {
        var offeredTools = System.Threading.Volatile.Read(ref _offeredTools).Tools;

        if (_mcp is null || offeredTools.IsDefaultOrEmpty)
            return;

        // Signature based on tool names + schema. If input schema changes, we must refresh.
        var names = offeredTools
            .Select(t => t.Name)
            .Where(n => _mcp.CanInvoke(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var schemaSig = offeredTools
            .Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.InputSchema.GetRawText())
            .ToArray();

        var sig = string.Join("\n", names) + "\n---\n" + string.Join("\n", schemaSig);
        if (string.Equals(sig, _mcpSignature, StringComparison.Ordinal))
            return;

        _mcpSignature = sig;

        var allowed = names.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        _runspace.SessionStateProxy.SetVariable("__mcpCtx", new McpToolPsContext(_mcp, allowed));

        // Determine per-server modules.
        var scripts = McpProxyModuleGenerator.Generate(offeredTools, verbs: McpApprovedVerbs.CreateDefault());
        var desiredServers = scripts.Keys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;

        // Remove modules for servers that disappeared.
        foreach (var oldServer in _mcpServers.Except(desiredServers, StringComparer.OrdinalIgnoreCase))
        {
            ps.Commands.Clear();
            ps.AddScript($"Remove-Module -Name 'Mcp.{oldServer}' -Force -ErrorAction SilentlyContinue");
            ps.Invoke();
        }

        // Import/update desired modules.
        foreach (var (server, script) in scripts)
        {
            _runspace.SessionStateProxy.SetVariable("__mcp_module_script", script);

            ps.Commands.Clear();
            ps.AddScript($"Remove-Module -Name 'Mcp.{server}' -Force -ErrorAction SilentlyContinue; $sb=[scriptblock]::Create($global:__mcp_module_script); $m=New-Module -Name 'Mcp.{server}' -ScriptBlock $sb; Import-Module $m -Force -Global | Out-Null");
            ps.Invoke();
        }

        _mcpServers = desiredServers;
    }

    public PowerShellExecutionResult Execute(string script, CancellationToken cancellationToken)
    {
        if (script is null) throw new ArgumentNullException(nameof(script));

        lock (_gate)
        {
            // Tool catalog can change between calls (capabilities, MCP discovery).
            RefreshMcpProxyModules();

            // Re-anchor location at the start of every call.
            try
            {
                if (_runspace.SessionStateProxy.Drive.Get("project") is not null)
                    _runspace.SessionStateProxy.Path.SetLocation("project:\\");
                else
                _runspace.SessionStateProxy.Path.SetLocation("sandbox:\\");
            }
            catch
            {
                // ignore
            }

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // Script executes in the existing runspace so state persists.
            ps.AddScript(script);

            var stdoutLines = new List<string>();
            var stderrLines = new List<string>();

            // Collect stream output.
            ps.Streams.Error.DataAdded += (_, e) =>
            {
                try
                {
                    var rec = ps.Streams.Error[e.Index];
                    stderrLines.Add(rec.ToString());
                }
                catch
                {
                    // ignore
                }
            };

            // NOTE: PS doesn't expose a first-class cancellation-aware Invoke().
            // We use BeginInvoke + wait + Stop() on cancellation.
            IAsyncResult ar;
            try
            {
                ar = ps.BeginInvoke();
            }
            catch (RuntimeException ex)
            {
                return new PowerShellExecutionResult(
                    Stdout: "",
                    Stderr: ex.ToString(),
                    Success: false);
            }

            using var reg = cancellationToken.Register(() =>
            {
                try { ps.Stop(); } catch { /* ignore */ }
            });

            PSDataCollection<PSObject> results;
            try
            {
                results = ps.EndInvoke(ar);
            }
            catch (RuntimeException ex)
            {
                stderrLines.Add(ex.ToString());
                results = new PSDataCollection<PSObject>();
            }

            foreach (var r in results)
            {
                if (r is null) continue;
                stdoutLines.Add(r.ToString());
            }

            var stdout = string.Join("\n", stdoutLines).TrimEnd();
            var stderr = string.Join("\n", stderrLines).TrimEnd();

            var success = !ps.HadErrors && stderr.Length == 0;
            return new PowerShellExecutionResult(stdout, stderr, success);
        }
    }

    public void Dispose()
    {
        try { _runspace.Dispose(); } catch { /* ignore */ }
    }
}

public sealed record PowerShellExecutionResult(
    string Stdout,
    string Stderr,
    bool Success);
