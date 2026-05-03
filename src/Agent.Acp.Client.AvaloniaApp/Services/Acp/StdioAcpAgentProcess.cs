using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Acp;
using Agent.Acp.Transport;

namespace Agent.Acp.Client.AvaloniaApp.Services.Acp;

/// <summary>
/// Launches an ACP agent over stdio (line-delimited JSON-RPC).
/// </summary>
public sealed class StdioAcpAgentProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly LineDelimitedStreamTransport _transport;

    public StdioAcpAgentProcess(Process process, LineDelimitedStreamTransport transport, AcpClientConnection connection)
    {
        _process = process;
        _transport = transport;
        Connection = connection;
    }

    public AcpClientConnection Connection { get; }

    public static Task<StdioAcpAgentProcess> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        // Process.Start is sync; keep method async-friendly anyway.
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        var p = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!p.Start())
            throw new InvalidOperationException("Failed to start ACP agent process.");

        var transport = new LineDelimitedStreamTransport(
            input: p.StandardOutput.BaseStream,
            output: p.StandardInput.BaseStream,
            name: "stdio-client");

        var conn = new AcpClientConnection(transport);

        // Drain stderr to avoid deadlocks on verbose agents.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !p.HasExited)
                {
                    var line = await p.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null) break;
                    // ignore for now; future: pipe to UI log
                }
            }
            catch
            {
                // ignore
            }
        }, cancellationToken);

        return Task.FromResult(new StdioAcpAgentProcess(p, transport, conn));
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _process.Dispose();
        }
    }
}
