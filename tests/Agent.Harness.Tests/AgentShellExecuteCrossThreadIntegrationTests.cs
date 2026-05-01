using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class AgentShellExecuteCrossThreadIntegrationTests
{
    [Fact]
    public async Task AgentShellExecute_StateIsPerThread_ButWorkingDirectoryIsSharedAcrossThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-pwsh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = new JsonlSessionStore(root);
        store.CreateNew(
            sessionId: "s1",
            metadata: new SessionMetadata(
                SessionId: "s1",
                Cwd: root,
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute);
        var state = SessionState.Empty with { Tools = tools };

        var execA = NewExecutor(store, threadId: "tA");
        var execB = NewExecutor(store, threadId: "tB");

        // Thread A sets a variable (should NOT be visible to thread B).
        {
            var res = await RunPs(execA, state, "call_a1", "$x = 41");
            res.GetProperty("ok").GetBoolean().Should().BeTrue();
        }

        // Thread B checks variable (should be null).
        {
            var res = await RunPs(execB, state, "call_b1", "if ($null -eq $x) { 'null' } else { $x }");
            res.GetProperty("stdout").GetString().Should().Be("null");
        }

        // Thread A writes to sandbox working directory (should be visible to thread B).
        {
            var res = await RunPs(execA, state, "call_a2", "'hello' | Set-Content -Path shared.txt; Get-Content -Path shared.txt");
            res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
            res.GetProperty("stdout").GetString().Should().Be("hello", res.GetProperty("stderr").GetString());
        }

        // Thread B reads the same file (shared working directory).
        {
            var res = await RunPs(execB, state, "call_b2", "Get-Content -Path shared.txt");
            res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
            res.GetProperty("stdout").GetString().Should().Be("hello", res.GetProperty("stderr").GetString());
        }

        // And the file exists under the session-scoped working dir.
        var expectedPath = Path.Combine(root, "s1", "pwsh_work", "shared.txt");
        File.Exists(expectedPath).Should().BeTrue($"expected shared working file at {expectedPath}");
    }

    private static HarnessEffectExecutor NewExecutor(JsonlSessionStore store, string threadId)
        => new(
            sessionId: "s1",
            client: new NullAcpClientCaller(new ClientCapabilities()),
            chat: new NullMeaiChatClient(),
            store: store,
            threadId: threadId);

    private static async Task<JsonElement> RunPs(HarnessEffectExecutor exec, SessionState state, string toolId, string script)
    {
        var observed = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall(toolId, ToolSchemas.AgentShellExecute.Name, new { script }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        completed.ToolId.Should().Be(toolId);

        completed.Result.Should().BeOfType<JsonElement>();
        return (JsonElement)completed.Result;
    }

    private sealed class NullMeaiChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NullAcpClientCaller(ClientCapabilities caps) : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unsupported method: {method}");
    }
}
