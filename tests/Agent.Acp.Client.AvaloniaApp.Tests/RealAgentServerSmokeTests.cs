using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Xunit;
using Xunit.Sdk;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class RealAgentServerSmokeTests
{
    [Fact]
    public async Task InitializeAndNewSession_AgainstRealAgentServer_Works()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ACP_REAL_AGENT_SMOKE"), "1", StringComparison.Ordinal))
            throw SkipException.ForSkip("Set ACP_REAL_AGENT_SMOKE=1 to run real Agent.Server smoke test.");

        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.."));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --project src/Agent.Server -c Release",
            WorkingDirectory = repoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var process = await StdioAcpAgentProcess.StartAsync(psi, cts.Token);

        var init = await AcpClientBootstrap.InitializeAsync(process.Connection, cts.Token);
        Assert.NotNull(init);

        var session = await AcpClientBootstrap.NewSessionAsync(process.Connection, repoRoot, cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
    }
}
