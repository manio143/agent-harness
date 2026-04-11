using System.Text.RegularExpressions;
using Xunit;

namespace Agent.Acp.Tests;

public class GeneratedModelQualityGateTests
{
    [Fact]
    public void AcpSchema_ShouldNotContain_KnownPlaceholderTypes()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var path = Path.Combine(repoRoot, "src", "Agent.Acp", "Generated", "AcpSchema.g.cs");
        Assert.True(File.Exists(path), $"Expected generated schema at: {path}");

        var code = File.ReadAllText(path);

        // Known NJsonSchema placeholder patterns we want to keep out of the public model surface.
        // Match placeholder as a *type* token, not a property name like "Content1".
        Assert.DoesNotMatch(new Regex(@"\bpublic\s+Content\d+\s+\w+\b", RegexOptions.Compiled), code);

        // Placeholders that look like the intended type name (no suffix) but are wrong.
        Assert.DoesNotMatch(new Regex(@"\bpublic\s+Outcome\s+Outcome\b", RegexOptions.Compiled), code);
        Assert.DoesNotMatch(new Regex(@"\bpublic\s+Update\s+Update\b", RegexOptions.Compiled), code);
    }

    private static string FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Agent.slnx");
            if (File.Exists(candidate))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root (Agent.slnx not found).");
    }
}
