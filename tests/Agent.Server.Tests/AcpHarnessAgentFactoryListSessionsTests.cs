using Agent.Acp.Schema;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Server.Tests;

public sealed class AcpHarnessAgentFactoryListSessionsTests
{
    [Fact]
    public async Task ListSessionsAsync_ListsSessionsFromProcessCwdStoreDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "ahaf-list", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);

        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(cwd);

        try
        {
            var opts = new AgentServerOptions
            {
                Sessions = new AgentServerOptions.SessionStoreOptions { Directory = ".agent/sessions" },
            };

            var rootDir = Path.GetFullPath(Path.Combine(cwd, opts.Sessions.Directory));
            var store = new JsonlSessionStore(rootDir);

            store.CreateNew("s1", new SessionMetadata("s1", "/repo", "One", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z"));
            store.CreateNew("s2", new SessionMetadata("s2", "/repo", null, "2026-01-03T00:00:00Z", "2026-01-04T00:00:00Z"));

            var factory = new AcpHarnessAgentFactory(new NoopChatClient(), opts);

            var resp = await factory.ListSessionsAsync(new ListSessionsRequest(), CancellationToken.None);

            resp.Sessions.Should().HaveCount(2);
            resp.Sessions.Select(s => s.SessionId).Should().BeEquivalentTo(new[] { "s1", "s2" });

            var one = resp.Sessions.Single(s => s.SessionId == "s1");
            one.Cwd.Should().Be("/repo");
            one.Title.Should().Be("One");
            one.UpdatedAt.Should().Be("2026-01-02T00:00:00Z");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return YieldNone();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldNone()
            {
                await Task.CompletedTask;
                yield break;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
