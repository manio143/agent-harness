using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;
using Microsoft.Extensions.AI;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;

namespace Agent.Harness.Tests;

public sealed class AcpEffectExecutorTests
{
    [Fact]
    public async Task CallModel_RendersPromptViaCore_AndCallsChatClientWithRenderedMessages()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new UserMessage("hi"),
                new AssistantMessage("hello"))
        };

        var chat = new RecordingMeaiChatClient();
        var exec = new AcpEffectExecutor(new FakeClientCaller(), chat);

        var observed = await exec.ExecuteAsync(state, new CallModel(), CancellationToken.None);
        observed.Should().NotBeNull();

        var expected = Core.RenderPrompt(state)
            .Select(m => (m.Role, m.Text))
            .ToArray();

        chat.Calls.Should().HaveCount(1);
        var actual = chat.Calls[0]
            .Select(m => (Role: m.Role.ToString()!.ToLowerInvariant(), m.Text))
            .ToArray();

        actual.Length.Should().Be(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var expRole = expected[i].Role switch
            {
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                _ => "system",
            };

            actual[i].Role.Should().Be(expRole);
            actual[i].Text.Should().Be(expected[i].Text);
        }
    }

    private sealed class RecordingMeaiChatClient : MeaiIChatClient
    {
        public List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> Calls { get; } = new();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add(list);

            async IAsyncEnumerable<ChatResponseUpdate> Empty()
            {
                yield break;
            }

            return Empty();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }

    private sealed class FakeClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => new();

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Not needed for CallModel test");
        }

        public Task RequestAsync<TRequest>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Not needed for CallModel test");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
