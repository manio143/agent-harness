using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Agent.Harness.Tests.TestChatClients;

/// <summary>
/// Test chat client that blocks until released, allowing tests to assert concurrency invariants.
/// Tracks maximum number of concurrent in-flight streaming calls.
/// </summary>
public sealed class BlockingChatClient : MeaiIChatClient
{
    private int _inFlight;

    public int ConcurrentCallsMax { get; private set; }

    public TaskCompletionSource<bool> FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        MeaiChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = Interlocked.Increment(ref _inFlight);
        ConcurrentCallsMax = Math.Max(ConcurrentCallsMax, now);
        FirstCallStarted.TrySetResult(true);

        try
        {
            await AllowCompletion.Task.WaitAsync(cancellationToken);
            yield break;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Task<MeaiChatResponse> GetResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        MeaiChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
