using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class RandomSuffixThreadIdAllocatorTests
{
    [Fact]
    public void AllocateThreadId_RetriesOnCollision_UsingThreadToolsExistenceCheck()
    {
        var sessionId = "sess";
        var store = new InMemoryThreadStore();
        store.CreateMainIfMissing(sessionId);

        // Pre-create colliding id.
        store.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: "child-0000",
            ParentThreadId: ThreadIds.Main,
            Intent: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t0",
            Mode: ThreadMode.Multi,
            Model: "default"));

        var tools = new ThreadManager(sessionId, store);
        var gen = new SequenceHexSuffixGenerator(["0000", "0001"]);

        var alloc = new RandomSuffixThreadIdAllocator(tools, gen, suffixChars: 4, maxAttempts: 5);

        alloc.AllocateThreadId("child").Should().Be("child-0001");
    }

    private sealed class SequenceHexSuffixGenerator(string[] values) : IHexSuffixGenerator
    {
        private int _i;

        public string NextHex(int chars)
        {
            if (_i >= values.Length)
                throw new InvalidOperationException("test generator exhausted");

            var v = values[_i++];
            v.Length.Should().Be(chars);
            return v;
        }
    }
}
