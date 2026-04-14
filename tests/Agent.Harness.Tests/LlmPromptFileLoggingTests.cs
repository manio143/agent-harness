using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class LlmPromptFileLoggingTests
{
    [Fact]
    public async Task WhenLogLlmPromptsEnabled_WritesPromptPayloadToSessionFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "marian-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata(
            SessionId: "s1",
            Cwd: "/repo/cwd",
            Title: null,
            CreatedAtIso: "2026-01-01T00:00:00.0000000+00:00",
            UpdatedAtIso: "2026-01-01T00:00:00.0000000+00:00"));

        var client = new NullClientCaller();
        var chat = new EmptyStreamingChatClient();

        var exec = new AcpEffectExecutor(
            sessionId: "s1",
            client: client,
            chat: chat,
            logLlmPrompts: true,
            sessionCwd: "/repo/cwd",
            store: store);

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new UserMessage("hi")),
            Tools = ImmutableArray.Create(new ToolDefinition(
                "read_text_file",
                "",
                JsonSerializer.SerializeToElement(new { type = "object" }))),
        };

        await exec.ExecuteAsync(state, new CallModel(), CancellationToken.None);

        var promptPath = Path.Combine(root, "s1", "llm.prompt.jsonl");
        File.Exists(promptPath).Should().BeTrue();

        var line = File.ReadLines(promptPath).Last();
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.TryGetProperty("messages", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("tools", out _).Should().BeTrue();
    }

    private sealed class EmptyStreamingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NullClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new()
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException(method);
        }
    }
}
