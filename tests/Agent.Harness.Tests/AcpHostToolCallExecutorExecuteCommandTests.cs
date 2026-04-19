using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpHostToolCallExecutorExecuteCommandTests
{
    [Fact]
    public async Task ExecuteCommand_HappyPath_ReturnsOutputAndExitStatus()
    {
        var caps = new ClientCapabilities { Terminal = true, Fs = new FileSystemCapabilities() };
        var client = new FakeAcpClientCaller(caps);

        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new InMemorySessionStore());

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "execute_command", new { command = "echo", args = new[] { "hi" } }), CancellationToken.None);

        var completed = obs.OfType<ObservedToolCallCompleted>().Single();
        var json = (System.Text.Json.JsonElement)completed.Result;
        json.GetProperty("exitStatus").GetProperty("code").GetInt32().Should().Be(0);
        json.GetProperty("output").GetString().Should().Contain("hi");
    }

    [Fact]
    public async Task ExecuteCommand_WhenMissingCommand_ReturnsFailed()
    {
        var caps = new ClientCapabilities { Terminal = true, Fs = new FileSystemCapabilities() };
        var client = new FakeAcpClientCaller(caps);

        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new InMemorySessionStore());

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "execute_command", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("missing_required:command");
    }

    private sealed class FakeAcpClientCaller(ClientCapabilities caps) : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            switch (method)
            {
                case "terminal/create":
                    return Task.FromResult((TResponse)(object)new CreateTerminalResponse { TerminalId = "term_1" });

                case "terminal/wait_for_exit":
                    return Task.FromResult((TResponse)(object)new WaitForTerminalExitResponse());

                case "terminal/output":
                    return Task.FromResult((TResponse)(object)new TerminalOutputResponse { ExitStatus = new ExitStatus { AdditionalProperties = { ["code"] = 0 } }, Output = "hi\n", Truncated = false });

                default:
                    throw new NotSupportedException(method);
            }
        }
    }

    private sealed class InMemorySessionStore : Agent.Harness.Persistence.ISessionStore
    {
        public void CreateNew(string sessionId, SessionMetadata metadata) { }

        public bool Exists(string sessionId) => true;

        public ImmutableArray<string> ListSessionIds() => ImmutableArray<string>.Empty;

        public SessionMetadata? TryLoadMetadata(string sessionId) => new(sessionId, "/cwd", null, "", "");

        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) => ImmutableArray<SessionEvent>.Empty;

        public void AppendCommitted(string sessionId, SessionEvent evt) { }

        public void UpdateMetadata(string sessionId, SessionMetadata metadata) { }
    }
}
