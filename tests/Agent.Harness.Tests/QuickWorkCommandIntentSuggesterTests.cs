using System.Collections.Immutable;
using Agent.Harness.Llm.CommandSuggestions;
using FluentAssertions;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class QuickWorkCommandIntentSuggesterTests
{
    [Fact]
    public async Task SuggestAsync_WhenResponseIsNotStrictJson_ReturnsEmptyAndDoesNotRetry()
    {
        var chat = new SequenceChatClient(new[]
        {
            "Sure! Here you go:\n[{\"name\":\"Get-ChildItem\",\"reason\":\"Lists files\"}]\nThanks!",
        });

        var ps = new FakePowerShellCommandCatalog();
        var sut = new QuickWorkCommandIntentSuggester(chat, ps);

        var tools = ImmutableArray<ToolDefinition>.Empty;
        var suggestions = await sut.SuggestAsync("list files", tools, CancellationToken.None);

        suggestions.Should().BeEmpty();
        chat.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SuggestAsync_WhenResponseIsStrictJson_ReturnsSuggestions()
    {
        var chat = new SequenceChatClient(new[]
        {
            "[{\"name\":\"Get-ChildItem\",\"reason\":\"Lists files\"}]",
        });

        var ps = new FakePowerShellCommandCatalog();
        var sut = new QuickWorkCommandIntentSuggester(chat, ps);

        var tools = ImmutableArray<ToolDefinition>.Empty;
        var suggestions = await sut.SuggestAsync("list files", tools, CancellationToken.None);

        suggestions.Should().BeEquivalentTo(new[] { new CommandSuggestion("Get-ChildItem", "Lists files") });
        chat.CallCount.Should().Be(1);
    }

    private sealed class SequenceChatClient : MeaiIChatClient
    {
        private readonly Queue<string> _responses;
        public int CallCount { get; private set; }

        public SequenceChatClient(IEnumerable<string> responses) => _responses = new Queue<string>(responses);

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var text = _responses.Dequeue();
            var msg = new MeaiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, (string?)"")
            {
                Contents = new List<MeaiAIContent> { new MeaiTextContent(text) },
            };
            return Task.FromResult(new MeaiChatResponse(new[] { msg }));
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakePowerShellCommandCatalog : IPowerShellCommandCatalog
    {
        public Task<ImmutableArray<PowerShellBuiltinCatalog.CmdletInfo>> GetCmdletsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray<PowerShellBuiltinCatalog.CmdletInfo>.Empty);

        public void Warmup() { }
    }
}
