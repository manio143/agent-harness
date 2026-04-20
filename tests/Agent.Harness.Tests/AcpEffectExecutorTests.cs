using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;
using Microsoft.Extensions.AI;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorTests
{
    [Fact]
    public async Task CallModel_RendersPromptViaMeaiPromptRenderer_AndCallsChatClientWithRenderedMessages()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new UserMessage("hi"),
                new AssistantMessage("hello"))
        };

        var chat = new RecordingMeaiChatClient();
        var store = new FakeSessionStore();
        store.CreateNew("sess1", new Agent.Harness.Persistence.SessionMetadata(
            SessionId: "sess1",
            Cwd: "/cwd",
            Title: null,
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var exec = new HarnessEffectExecutor("sess1", new FakeClientCaller(), chat, store: store);

        var observed = await exec.ExecuteAsync(state, new CallModel("default"), CancellationToken.None);
        observed.Should().NotBeNull();

        var expected = Agent.Harness.Llm.MeaiPromptRenderer.Render(state)
            .Select(m => m.Role)
            .ToArray();

        chat.Calls.Should().HaveCount(1);
        var actual = chat.Calls[0].ToArray();

        // First messages are system prompt fragments. Order must be stable for provider-side prefix caching.
        actual[0].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.System);
        actual[0].Text.Should().Contain("Tool calling policy:");
        actual[0].Text.Should().Contain("report_intent");

        actual[1].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.System);
        actual[1].Text.Should().Contain("<session>");
        actual[1].Text.Should().Contain("\"sessionId\":\"sess1\"");
        actual[1].Text.Should().Contain("\"cwd\":\"/cwd\"");
        actual[1].Text.Should().Contain("\"createdAtIso\":\"t0\"");
        actual[1].Text.Should().Contain("\"updatedAtIso\":\"t1\"");
        actual[1].Text.Should().NotContain("\"title\"");
        actual[1].Text.Should().NotContain("\"tools\"");

        actual.Length.Should().Be(expected.Length + 2);
        for (var i = 0; i < expected.Length; i++)
        {
            actual[i + 2].Role.Should().Be(expected[i]);
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
            options.Should().NotBeNull();
            options!.ToolMode.Should().Be(ChatToolMode.Auto);

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

    private sealed class FakeSessionStore : Agent.Harness.Persistence.ISessionStore
    {
        private readonly Dictionary<string, Agent.Harness.Persistence.SessionMetadata> _meta = new();

        public void CreateNew(string sessionId, Agent.Harness.Persistence.SessionMetadata metadata) => _meta[sessionId] = metadata;
        public bool Exists(string sessionId) => _meta.ContainsKey(sessionId);
        public ImmutableArray<string> ListSessionIds() => _meta.Keys.ToImmutableArray();
        public Agent.Harness.Persistence.SessionMetadata? TryLoadMetadata(string sessionId) => _meta.TryGetValue(sessionId, out var m) ? m : null;
        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) => ImmutableArray<SessionEvent>.Empty;
        public void AppendCommitted(string sessionId, SessionEvent evt) { }
        public void UpdateMetadata(string sessionId, Agent.Harness.Persistence.SessionMetadata metadata) => _meta[sessionId] = metadata;
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
