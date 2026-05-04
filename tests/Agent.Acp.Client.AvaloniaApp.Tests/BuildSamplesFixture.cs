using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class BuildSamplesFixture
{
    public BuildSamplesFixture()
    {
        var repo = GetRepoRoot();

        Build(repo, Path.Combine(repo, "samples", "Acp.MinimalAgent", "Acp.MinimalAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.SessionListAgent", "Acp.SessionListAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.SessionListFlakyAgent", "Acp.SessionListFlakyAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.StreamingAgent", "Acp.StreamingAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.ToolLifecycleAgent", "Acp.ToolLifecycleAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.PromptFailAgent", "Acp.PromptFailAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.StoryAgent", "Acp.StoryAgent.csproj"));
        Build(repo, Path.Combine(repo, "samples", "Acp.InterleavingAgent", "Acp.InterleavingAgent.csproj"));
    }

    private static void Build(string repo, string csproj)
    {
        Assert.True(File.Exists(csproj));

        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{csproj}\" -c Release",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Assert.NotNull(p);
        var exited = p!.WaitForExit(1000 * 60);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"Timed out building sample {Path.GetFileNameWithoutExtension(csproj)} (dotnet build -c Release)");
        }

        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new Exception($"Failed to build sample {Path.GetFileNameWithoutExtension(csproj)}: {err}");
        }
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx)");
    }
}

[CollectionDefinition("samples")]
public sealed class SamplesCollection : ICollectionFixture<BuildSamplesFixture> { }
