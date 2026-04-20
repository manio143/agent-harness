using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Llm.SystemPrompts;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorSystemPromptOrderIntegrationTests
{
    private sealed class NullClientCaller : Agent.Acp.Acp.IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ACP client should not be used in this test");
    }

    private sealed class ConstContributor(string id, int order, string content) : ISystemPromptContributor
    {
        public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
            => new[] { new SystemPromptFragment(id, order, content) };
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

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
    }

    [Fact]
    public async Task CallModel_WritesPromptLog_WithStableSystemPromptOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-sys-prompts", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);

        var sessionId = "s1";
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threadStore = new Agent.Harness.Threads.InMemoryThreadStore();
        threadStore.CreateMainIfMissing(sessionId);
        var existingMeta = threadStore.TryLoadThreadMetadata(sessionId, "main")!;
        threadStore.SaveThreadMetadata(sessionId, existingMeta with { CreatedAtIso = "t0", UpdatedAtIso = "t1" });

        var composer = new SystemPromptComposer(new ISystemPromptContributor[]
        {
            new ConstContributor("model_catalog", 1000, "Available inference models: qwen. Default: qwen. Quick-work: granite."),
            new ToolCallingPolicySystemPromptContributor(),
            new SessionEnvelopeSystemPromptContributor(),
            new ThreadEnvelopeSystemPromptContributor(),
        });

        var exec = new HarnessEffectExecutor(
            sessionId: sessionId,
            client: new NullClientCaller(),
            chat: new NoOpChatClient(),
            chatByModel: null,
            isKnownModel: null,
            mcp: null,
            logLlmPrompts: true,
            sessionCwd: "/tmp",
            store: store,
            modelCatalogSystemPrompt: null,
            systemPromptComposer: composer,
            threadStore: threadStore);


        // Seed minimal state; tools empty ok.
        var state = new SessionState(
            Committed: ImmutableArray<SessionEvent>.Empty.Add(new UserMessage("hi")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        await exec.ExecuteAsync(state, new CallModel("default"), CancellationToken.None);

        var path = Path.Combine(root, sessionId, "llm.prompt.jsonl");
        File.Exists(path).Should().BeTrue();

        var last = File.ReadAllLines(path).Last();
        using var doc = JsonDocument.Parse(last);

        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().BeGreaterOrEqualTo(4);

        var m0 = messages[0];
        var m1 = messages[1];
        var m2 = messages[2];
        var m3 = messages[3];

        m0.GetProperty("role").GetString().Should().Be("system");
        m0.GetProperty("text").GetString().Should().Contain("Available inference models:");

        m1.GetProperty("role").GetString().Should().Be("system");
        m1.GetProperty("text").GetString().Should().Contain("You MUST call `report_intent` before calling any other tool");

        m2.GetProperty("role").GetString().Should().Be("system");
        m2.GetProperty("text").GetString().Should().StartWith("<session>");

        m3.GetProperty("role").GetString().Should().Be("system");
        m3.GetProperty("text").GetString().Should().StartWith("<thread>");
        m3.GetProperty("text").GetString().Should().Contain("\"createdAtIso\":\"t0\"");
    }
}
