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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var process = await StdioAcpAgentProcess.StartAsync(psi, cts.Token);

        var init = await AcpClientBootstrap.InitializeAsync(process.Connection, cts.Token);
        Assert.NotNull(init);

        var session = await AcpClientBootstrap.NewSessionAsync(process.Connection, repoRoot, cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
    }

    [Fact]
    public async Task SessionLoad_ReplaysCommittedHistory_ViaSessionUpdateNotifications()
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

        // 1) Start agent, create a session, and commit at least one stable message.
        string sessionId;
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var p1 = await StdioAcpAgentProcess.StartAsync(psi, cts.Token);

            _ = await AcpClientBootstrap.InitializeAsync(p1.Connection, cts.Token);

            var session = await AcpClientBootstrap.NewSessionAsync(p1.Connection, repoRoot, cts.Token);
            sessionId = session.SessionId;

            // Commit stable history directly into the session store (events.jsonl), so replay is deterministic and doesn't depend on LLM auth.
            var threadEventsPath = System.IO.Path.Combine(repoRoot, ".agent", "sessions", sessionId, "threads", "main", "events.jsonl");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(threadEventsPath)!);
            System.IO.File.AppendAllText(threadEventsPath, "{\"type\":\"user_message\",\"text\":\"hello\"}\n");
            System.IO.File.AppendAllText(threadEventsPath, "{\"type\":\"assistant_message\",\"text\":\"world\"}\n");
        }

        // 2) Restart agent process and load the existing session. Expect replay via session/update.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var p2 = await StdioAcpAgentProcess.StartAsync(psi, cts2.Token);

        _ = await AcpClientBootstrap.InitializeAsync(p2.Connection, cts2.Token);

        var gotReplay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        p2.Connection.NotificationReceived += notif =>
        {
            if (!string.Equals(notif.Method, "session/update", StringComparison.Ordinal))
                return;

            if (notif.Params is null)
                return;

            var el = notif.Params.Value;
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
                return;

            if (!el.TryGetProperty("sessionId", out var sidEl) || sidEl.ValueKind != System.Text.Json.JsonValueKind.String)
                return;

            if (!string.Equals(sidEl.GetString(), sessionId, StringComparison.Ordinal))
                return;

            // Any update for this session during load indicates replay.
            gotReplay.TrySetResult(true);
        };

        var loadTask = AcpClientBootstrap.LoadSessionAsync(p2.Connection, sessionId, repoRoot, cts2.Token);

        // ACP contract: replay happens before completing session/load.
        var completed = await Task.WhenAny(gotReplay.Task, loadTask);
        Assert.True(completed == gotReplay.Task, "Expected session/update replay notification before session/load completed.");

        _ = await loadTask;
    }
}
