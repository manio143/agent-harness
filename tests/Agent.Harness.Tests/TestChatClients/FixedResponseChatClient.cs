using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests.TestChatClients;

public sealed class FixedResponseChatClient : MeaiIChatClient
{
    private readonly string _text;

    public FixedResponseChatClient(string text) => _text = text;

    public Task<MeaiChatResponse> GetResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        MeaiChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // SessionTitleGenerator uses GetResponseAsync.
        var msg = new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, _text);
        return Task.FromResult(new MeaiChatResponse(new[] { msg }));
    }

    public async IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        MeaiChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
