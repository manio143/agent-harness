using Agent.Harness;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SessionCoreHelloTests
{
    [Fact]
    public async Task GivenEmptySession_WhenUserSaysHello_LogsUserAndCallsModel_ThenLogsAssistant()
    {
        var log = new InMemoryEventLog();
        var chat = new ScriptedChatClient().WhenCalledReturn("Hello back");

        var core = new SessionCore(log, chat, new SessionCoreOptions(EmitModelInvokedEvents: true));

        var result = await core.HandleUserMessageAsync("Hello", CancellationToken.None);

        result.AssistantText.Should().Be("Hello back");

        log.Events.Should().HaveCount(3);
        log.Events[0].Should().BeOfType<UserMessageAdded>().Which.Text.Should().Be("Hello");

        var invoked = log.Events[1].Should().BeOfType<ModelInvoked>().Which;
        invoked.RenderedMessages.Should().Equal(new ChatMessage(ChatRole.User, "Hello"));

        log.Events[2].Should().BeOfType<AssistantMessageAdded>().Which.Text.Should().Be("Hello back");

        chat.Calls.Should().HaveCount(1);
        chat.Calls[0].Should().Equal(new ChatMessage(ChatRole.User, "Hello"));
    }

    [Fact]
    public async Task ModelInvokedEvent_IsNotEmitted_WhenDisabled()
    {
        var log = new InMemoryEventLog();
        var chat = new ScriptedChatClient().WhenCalledReturn("Hello back");

        var core = new SessionCore(log, chat, new SessionCoreOptions(EmitModelInvokedEvents: false));

        await core.HandleUserMessageAsync("Hello", CancellationToken.None);

        log.Events.Should().NotContain(e => e is ModelInvoked);
        log.Events.Should().Contain(new UserMessageAdded("Hello"));
        log.Events.Should().Contain(new AssistantMessageAdded("Hello back"));
    }
}
