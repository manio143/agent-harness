using System.Collections.Immutable;
using Agent.Harness.TitleGeneration;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SessionTitleGeneratorTests
{
    [Fact]
    public async Task Generates_title_once_after_first_assistant_message()
    {
        var chat = new ScriptedChatClient().WhenCalledReturn("My Title\n(second line ignored)");
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
        chat.Calls[0][0].Role.Should().Be(ChatRole.System);
        chat.Calls[0][0].Text.Should().Be(SessionTitleGenerator.SystemPrompt);
        chat.Calls[0][1].Role.Should().Be(ChatRole.User);
        chat.Calls[0][1].Text.Should().Contain("<conversation>");

        // If title already exists, do nothing.
        var state2 = state with { Committed = state.Committed.Add(evt!) };
        var evt2 = await gen.MaybeGenerateAfterTurnAsync(state2, CancellationToken.None);
        evt2.Should().BeNull();
    }
}
