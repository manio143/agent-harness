namespace Agent.Harness.Acp;

public static class SnakeCase
{
    public static string Normalize(string value)
    {
        var chars = value
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray();

        var s = new string(chars);

        // collapse runs
        while (s.Contains("__", StringComparison.Ordinal))
            s = s.Replace("__", "_", StringComparison.Ordinal);

        return s.Trim('_');
    }
}
