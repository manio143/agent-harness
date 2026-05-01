using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

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

    public InProcessPowerShellSession(string workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            throw new ArgumentException("workingDir is required", nameof(workingDir));

        _workingDir = workingDir;
        Directory.CreateDirectory(_workingDir);

        var iss = InitialSessionState.CreateDefault2();

        // Reduce the surface area for breaking out into arbitrary .NET.
        iss.LanguageMode = PSLanguageMode.ConstrainedLanguage;

        iss.StartupScripts.Add("$ErrorActionPreference = 'Stop'");

        _runspace = RunspaceFactory.CreateRunspace(iss);
        _runspace.Open();

        // Best-effort: expose ONLY a sandbox: drive rooted at the session working dir.
        // Drive manipulation is done after runspace open because InitialSessionState doesn't expose drives.
        try
        {
            foreach (var d in _runspace.SessionStateProxy.Drive.GetAll())
            {
                if (string.Equals(d.Name, "sandbox", StringComparison.Ordinal))
                    continue;

                try { _runspace.SessionStateProxy.Drive.Remove(d.Name, force: true, scope: "Global"); }
                catch { /* ignore */ }
            }

            // Recreate sandbox drive (idempotent: remove above will have removed it too).
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
            // ignore; the tool will still run, but with weaker filesystem containment.
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
