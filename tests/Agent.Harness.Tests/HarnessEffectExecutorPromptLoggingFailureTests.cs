using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorPromptLoggingFailureTests
{
    [Fact]
    public async Task CallModel_WhenLogPromptsEnabled_AndStoreIsNull_DoesNotThrow()
    {
        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities() }),
            chat: new RecordingChatClient(),
            logLlmPrompts: true,
            store: null);

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent),
        };

        var obs = await exec.ExecuteAsync(state, new CallModel("default"), CancellationToken.None);

        // End-of-stream completion should still be surfaced.
        obs.Should().Contain(o => o is ObservedAssistantMessageCompleted);
    }

    [Fact]
    public async Task CallModel_WhenPromptLogWriteFails_IsSwallowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "heec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Force sessionDir to be a file so Directory.CreateDirectory(sessionDir) throws.
        File.WriteAllText(Path.Combine(root, "s1"), "not-a-directory");

        var store = new JsonlSessionStore(root);

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities() }),
            chat: new RecordingChatClient(),
            logLlmPrompts: true,
            store: store);

        var state = SessionState.Empty with
        {
            Tools = ImmutableArray.Create(ToolSchemas.ReportIntent),
        };

        var obs = await exec.ExecuteAsync(state, new CallModel("default"), CancellationToken.None);

        obs.Should().Contain(o => o is ObservedAssistantMessageCompleted);
    }

    private sealed class RecordingChatClient : IChatClient
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
