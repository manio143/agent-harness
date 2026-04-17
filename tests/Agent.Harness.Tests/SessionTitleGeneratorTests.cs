using System.Collections.Immutable;
using Agent.Harness.TitleGeneration;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class SessionTitleGeneratorTests
{
    [Fact]
    public async Task Generates_title_once_after_first_assistant_message()
    {
        var chat = new ScriptedMeaiChatClient().WhenCalledReturn("My Title\n(second line ignored)");
        var gen = new SessionTitleGenerator(chat);

        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Hi"),
                new AssistantMessage("Hello")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var evt = await gen.MaybeGenerateAfterTurnAsync(state, CancellationToken.None);
        evt.Should().Be(new SessionTitleSet("My Title"));

        chat.Calls.Should().HaveCount(1);
        chat.Calls[0][0].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.System);
        chat.Calls[0][0].Text.Should().Be(SessionTitleGenerator.SystemPrompt);
        chat.Calls[0][1].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.User);
        chat.Calls[0][1].Text.Should().Contain("<conversation>");

        // If title already exists, do nothing.
        var state2 = state with { Committed = state.Committed.Add(evt!) };
        var evt2 = await gen.MaybeGenerateAfterTurnAsync(state2, CancellationToken.None);
        evt2.Should().BeNull();
    }

    private sealed class ScriptedMeaiChatClient : IChatClient
    {
        private string _next = "";

        public List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> Calls { get; } = new();

        public ScriptedMeaiChatClient WhenCalledReturn(string assistantText)
        {
            _next = assistantText;
            return this;
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add(list);

            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, _next)
            }));
        }

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
