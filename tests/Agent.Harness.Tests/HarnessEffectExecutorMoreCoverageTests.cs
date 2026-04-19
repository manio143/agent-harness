using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorMoreCoverageTests
{
    [Fact]
    public async Task ScheduleWake_EmitsObservedWakeModel()
    {
        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient());

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ScheduleWake("thr_x"), CancellationToken.None);

        obs.OfType<ObservedWakeModel>().Single().ThreadId.Should().Be("thr_x");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_WhenUnknownEffect_EmitsNothing()
    {
        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient());

        var obs = await exec.ExecuteAsync(SessionState.Empty, new UnknownEffect(), CancellationToken.None);

        obs.Should().BeEmpty();
    }

    private sealed record UnknownEffect() : Effect;

    private sealed class NullMeaiChatClient : IChatClient
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

    private sealed class NullAcpClientCaller(ClientCapabilities caps) : Agent.Acp.Acp.IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unsupported method: {method}");
    }
}
