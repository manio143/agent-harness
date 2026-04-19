using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorPromptLoggingContentsTests
{
    [Fact]
    public async Task CallModel_WhenLogPromptsEnabled_LogsFunctionCallAndFunctionResultContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "heec", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata("s1", "/tmp", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities() }),
            chat: new RecordingChatClient(),
            logLlmPrompts: true,
            store: store);

        var toolArgs = JsonSerializer.SerializeToElement(new { path = "/tmp/a.txt" });
        var toolResult = JsonSerializer.SerializeToElement(new { ok = true });

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new UserMessage("hi"),
                new ToolCallRequested("call_0", "read_text_file", toolArgs),
                new ToolCallCompleted("call_0", toolResult)),
            Tools = ImmutableArray.Create(ToolSchemas.ReadTextFile),
        };

        // Execution triggers prompt logging before streaming starts.
        await exec.ExecuteAsync(state, new CallModel("default"), CancellationToken.None);

        var logPath = Path.Combine(root, "s1", "llm.prompt.jsonl");
        File.Exists(logPath).Should().BeTrue();

        var log = File.ReadAllText(logPath);
        log.Should().Contain("\"type\":\"function_call\"");
        log.Should().Contain("\"type\":\"function_result\"");
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
