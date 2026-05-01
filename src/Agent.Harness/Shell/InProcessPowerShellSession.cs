using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Agent.Harness.Shell.Mcp;

namespace Agent.Harness.Shell;

/// <summary>
/// In-process PowerShell Core runspace used as a "virtual shell".
///
/// Notes / intent:
/// - Per-session (and per-thread) instance, so state (variables, functions) can persist.
/// - Best-effort filesystem containment via:
///   - ConstrainedLanguage
///   - A single FileSystem PSDrive rooted at a session-specific directory (sandbox:)
///
/// This is a tool-surface restriction, not a perfect sandbox.
/// </summary>
public sealed class InProcessPowerShellSession : IDisposable
{
    private readonly object _gate = new();
    private readonly string _workingDir;
    private readonly Runspace _runspace;

    public InProcessPowerShellSession(
        string workingDir,
        Agent.Acp.Acp.IAcpClientCaller? client = null,
        string? sessionId = null,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null,
        Agent.Harness.Acp.IMcpToolInvoker? mcp = null,
        System.Collections.Immutable.ImmutableArray<Agent.Harness.ToolDefinition> offeredTools = default)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            throw new ArgumentException("workingDir is required", nameof(workingDir));

        _workingDir = workingDir;
        Directory.CreateDirectory(_workingDir);

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

        // Register ACP-backed provider for client: drive (if ACP client context was provided).
        if (client is not null)
        {
            iss.Providers.Add(new SessionStateProviderEntry(
                name: "AcpClient",
                implementingType: typeof(AcpClientContentProvider),
                helpFileName: null));
        }

        // Full language mode: required for richer PowerShell UX (dynamic modules/functions)
        // and for invoking non-core methods during MCP proxy cmdlet bridging.
        // This is still a best-effort containment mechanism, not a hardened sandbox.
        iss.LanguageMode = PSLanguageMode.FullLanguage;

        iss.StartupScripts.Add("$ErrorActionPreference = 'Stop'");


        _runspace = RunspaceFactory.CreateRunspace(iss);
        _runspace.Open();

        // Attach ACP client context + create client: drive (rooted at session cwd).
        if (client is not null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidOperationException("client_drive_requires_sessionId");

            _runspace.SessionStateProxy.SetVariable("__acp_client_ctx", new AcpClientPsContext(sessionId!, client, sessionCwd, store));

            // Create the drive (no listing support; content operations map to ACP).
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;
                ps.AddScript("New-PSDrive -Name client -PSProvider AcpClient -Root / -Scope Global | Out-Null");
                ps.Invoke();
            }
            catch
            {
                // ignore: the shell still works without client:
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

            _runspace.SessionStateProxy.Path.SetLocation("sandbox:\\");
        }
        catch
        {
            // ignore
        }

        // MCP proxy cmdlets: attach context + define proxy functions.
        // IMPORTANT: avoid dynamic module creation (New-Module / ScriptBlock::Create),
        // because those are brittle under ConstrainedLanguage. Instead we define functions
        // directly in the runspace after it opens.
        if (mcp is not null && !offeredTools.IsDefaultOrEmpty)
        {
            try
            {
                var allowed = offeredTools
                    .Select(t => t.Name)
                    .Where(n => mcp.CanInvoke(n))
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

                _runspace.SessionStateProxy.SetVariable("__mcpCtx", new McpToolPsContext(mcp, allowed));

                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;

                var scripts = McpProxyModuleGenerator.Generate(offeredTools, verbs: McpApprovedVerbs.CreateDefault());
                foreach (var kvp in scripts)
                {
                    var server = kvp.Key;
                    var script = kvp.Value;

                    // Create + import a module per MCP server.
                    // FullLanguage mode allows ScriptBlock::Create + New-Module.
                    _runspace.SessionStateProxy.SetVariable("__mcp_module_script", script);

                    ps.Commands.Clear();
                    ps.AddScript($"$sb=[scriptblock]::Create($global:__mcp_module_script); $m=New-Module -Name 'Mcp.{server}' -ScriptBlock $sb; Import-Module $m -Force -Global | Out-Null");
                    ps.Invoke();
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    public PowerShellExecutionResult Execute(string script, CancellationToken cancellationToken)
    {
        if (script is null) throw new ArgumentNullException(nameof(script));

        lock (_gate)
        {
            // Re-anchor location at the start of every call.
            try
            {
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
