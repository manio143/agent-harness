using System.Collections.Immutable;
using System.Linq;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Server.Tests;

public sealed class AcpHarnessAgentFactoryReplaySessionTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public async Task ReplaySessionAsync_ReplaysStableCommittedMessages_FromMainThreadLog(bool publishReasoning, int expectedCount)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "ahaf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Acp = new AgentServerOptions.AcpOptions { PublishReasoning = publishReasoning },
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts);

        var newResp = await factory.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = new List<McpServer>(),
        }, CancellationToken.None);

        var sessionId = newResp.SessionId;

        // Write committed history to the MAIN THREAD log, not the session-level events.jsonl.
        var rootDir = Path.GetFullPath(Path.Combine(cwd, opts.Sessions.Directory));
        var threads = new JsonlThreadStore(rootDir);

        threads.AppendCommittedEvent(sessionId, ThreadIds.Main, new UserMessage("hi"));
        threads.AppendCommittedEvent(sessionId, ThreadIds.Main, new AssistantMessage("hello"));
        threads.AppendCommittedEvent(sessionId, ThreadIds.Main, new AssistantTextDelta("delta (should not replay)"));
        threads.AppendCommittedEvent(sessionId, ThreadIds.Main, new ReasoningTextDelta("thought"));

        var events = new CapturingSessionEvents();

        await factory.ReplaySessionAsync(sessionId, events, CancellationToken.None);

        events.Updates.Should().HaveCount(expectedCount);
        events.Updates.OfType<UserMessageChunk>().Single().Content.As<Agent.Acp.Schema.TextContent>().Text.Should().Be("hi");
        events.Updates.OfType<AgentMessageChunk>().Single().Content.As<Agent.Acp.Schema.TextContent>().Text.Should().Be("hello");

        if (publishReasoning)
            events.Updates.OfType<AgentThoughtChunk>().Single().Content.As<Agent.Acp.Schema.TextContent>().Text.Should().Be("thought");
        else
            events.Updates.OfType<AgentThoughtChunk>().Should().BeEmpty();

        // Deltas should not be replayed.
        var agentTexts = events.Updates
            .OfType<AgentMessageChunk>()
            .Select(c => c.Content.As<Agent.Acp.Schema.TextContent>().Text)
            .ToArray();

        agentTexts.Should().AllSatisfy(t => t.Should().NotContain("delta"));
    }

    [Fact]
    public async Task LoadSessionAsync_WhenSessionMissing_ThrowsJsonRpcInvalidParams()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "ahaf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var opts = new AgentServerOptions
        {
            Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
        };

        var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts);

        var act = async () => await factory.LoadSessionAsync(new LoadSessionRequest
        {
            SessionId = "missing",
            Cwd = cwd,
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<AcpJsonRpcException>()).Which.Code.Should().Be(-32602);
    }

    private sealed class CapturingSessionEvents : IAcpSessionEvents
    {
        public List<object> Updates { get; } = new();

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

file static class ChunkExtensions
{
    public static T As<T>(this object content) where T : class => (T)content;
}
