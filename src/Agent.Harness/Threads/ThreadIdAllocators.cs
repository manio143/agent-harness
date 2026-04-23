using System.Globalization;

namespace Agent.Harness.Threads;

public interface IHexSuffixGenerator
{
    string NextHex(int chars);
}

public sealed class GuidHexSuffixGenerator : IHexSuffixGenerator
{
    public string NextHex(int chars)
    {
        if (chars <= 0)
            throw new ArgumentOutOfRangeException(nameof(chars));
        if (chars > 32)
            throw new ArgumentOutOfRangeException(nameof(chars), "hex suffix cannot exceed 32 chars");

        return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..chars];
    }
}

/// <summary>
/// Allocates thread ids of the form: "{name}-{hhhh}".
/// Collisions are avoided by retrying with a new suffix when the id already exists.
/// </summary>
public sealed class RandomSuffixThreadIdAllocator : IThreadIdAllocator
{
    private readonly IThreadTools _threads;
    private readonly IHexSuffixGenerator _suffix;
    private readonly int _suffixChars;
    private readonly int _maxAttempts;

    public RandomSuffixThreadIdAllocator(
        IThreadTools threads,
        IHexSuffixGenerator suffix,
        int suffixChars = 4,
        int maxAttempts = 20)
    {
        _threads = threads;
        _suffix = suffix;
        _suffixChars = suffixChars;
        _maxAttempts = maxAttempts;
    }

    public string AllocateThreadId(string name)
    {
        for (var i = 0; i < _maxAttempts; i++)
        {
            var id = $"{name}-{_suffix.NextHex(_suffixChars)}";
            if (_threads.TryGetThreadMetadata(id) is null)
                return id;
        }

        throw new InvalidOperationException("thread_start.collision");
    }
}
