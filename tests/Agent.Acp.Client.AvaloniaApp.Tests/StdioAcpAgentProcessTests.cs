using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

[Collection("samples")]
public sealed class StdioAcpAgentProcessTests
{
    [Fact]
    public async Task Can_initialize_and_create_session_with_minimal_agent()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var repo = GetRepoRoot();

        // Ensure sample is built (assume it exists in repo).
        var exe = Path.Combine(repo, "samples", "Acp.MinimalAgent", "bin", "Release", "net8.0", "Acp.MinimalAgent");
        Assert.True(File.Exists(exe), $"Expected sample executable at {exe} (build samples/Acp.MinimalAgent -c Release)");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "",
            WorkingDirectory = repo,
        };

        await using var proc = await StdioAcpAgentProcess.StartAsync(psi, ct);

        var init = await AcpClientBootstrap.InitializeAsync(proc.Connection, ct);
        Assert.NotNull(init.AgentInfo);
        Assert.NotNull(init.AgentCapabilities);

        var session = await AcpClientBootstrap.NewSessionAsync(proc.Connection, cwd: repo, ct);
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}
