using System.Collections.Immutable;

namespace Agent.Harness.Llm.CommandSuggestions;

public interface IPowerShellCommandCatalog
{
    /// <summary>
    /// Returns PowerShell cmdlet names (builtin catalog) cached per-process.
    /// Implementation must be safe to call from any context, including during PowerShell pipeline execution.
    /// </summary>
    Task<ImmutableArray<string>> GetCmdletsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fire-and-forget warmup.
    /// </summary>
    void Warmup();
}

public sealed class PowerShellCommandCatalog : IPowerShellCommandCatalog
{
    private readonly object _gate = new();
    private Task<ImmutableArray<string>>? _task;

    public void Warmup()
    {
        _ = GetCmdletsAsync();
    }

    public Task<ImmutableArray<string>> GetCmdletsAsync(CancellationToken cancellationToken = default)
    {
        // Per-process caching.
        lock (_gate)
        {
            _task ??= Task.Run(() => PowerShellBuiltinCatalog.GetDefaultCmdlets(), CancellationToken.None);
            return _task;
        }
    }
}
