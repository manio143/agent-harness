using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class AgentShellExecuteProjectDriveTests
{
    [Fact]
    public async Task AgentShellExecute_ProjectDrive_AllowsDriveRootedPaths()
    {
        var projectDir = CreateTempProjectDir();
        var (exec, state) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "New-Item -ItemType Directory -Path project:\\dir -Force | Out-Null; Set-Content -Path project:\\dir\\file.txt -Value 'x'; Get-Content -Path project:\\dir\\file.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        res.GetProperty("stdout").GetString().Should().Be("x");
        File.ReadAllText(Path.Combine(projectDir, "dir", "file.txt")).Trim().Should().Be("x");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_NormalizesTraversalWithinDrive()
    {
        var projectDir = CreateTempProjectDir();
        var (exec, state) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path project:\\a\\..\\secret.txt -Value 'y'; Get-Content -Path project:\\secret.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        res.GetProperty("stdout").GetString().Should().Be("y");
        File.ReadAllText(Path.Combine(projectDir, "secret.txt")).Trim().Should().Be("y");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_AllowsFileListing()
    {
        var projectDir = CreateTempProjectDir();
        var childDir = Path.Combine(projectDir, "src");
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(childDir, "b.txt"), "b");

        var (exec, state) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-ChildItem -Path project:\\src | Sort-Object Name | Select-Object -ExpandProperty Name");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        res.GetProperty("stdout").GetString().Should().Be("a.txt\nb.txt");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_IsAvailableWithoutClientContext()
    {
        var projectDir = CreateTempProjectDir();
        var store = CreateStore("s1", projectDir);

        var handler = new Agent.Harness.Tools.Handlers.AgentShellExecuteToolHandler(
            sessionId: "s1",
            threadId: Agent.Harness.Threads.ThreadIds.Main,
            store: store,
            client: null,
            sessionCwd: projectDir);

        var observed = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", ToolSchemas.AgentShellExecute.Name, new { script = "Get-PSDrive -Name project | Select-Object -ExpandProperty Name" }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        var json = (JsonElement)completed.Result;

        json.GetProperty("ok").GetBoolean().Should().BeTrue(json.GetProperty("stderr").GetString());
        json.GetProperty("stdout").GetString().Should().Be("project");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_UsesStoredSessionCwd_WhenSessionCwdNotPassed()
    {
        var projectDir = CreateTempProjectDir();
        var store = CreateStore("s1", projectDir);

        var handler = new Agent.Harness.Tools.Handlers.AgentShellExecuteToolHandler(
            sessionId: "s1",
            threadId: Agent.Harness.Threads.ThreadIds.Main,
            store: store,
            client: null,
            sessionCwd: null);

        var observed = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", ToolSchemas.AgentShellExecute.Name, new { script = "Get-PSDrive -Name project | Select-Object -ExpandProperty Root" }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        var json = (JsonElement)completed.Result;

        json.GetProperty("ok").GetBoolean().Should().BeTrue(json.GetProperty("stderr").GetString());
        json.GetProperty("stdout").GetString().Should().Be(Path.GetFullPath(projectDir));
    }

    private static (HarnessEffectExecutor exec, SessionState state) Arrange(string cwd)
    {
        var store = CreateStore("s1", cwd);

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: new NullAcpClientCaller(),
            chat: new NullMeaiChatClient(),
            store: store,
            sessionCwd: cwd,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };
        return (exec, state);
    }

    private static JsonlSessionStore CreateStore(string sessionId, string cwd)
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "harness-projectdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var store = new JsonlSessionStore(storeRoot);
        store.CreateNew(
            sessionId: sessionId,
            metadata: new SessionMetadata(
                SessionId: sessionId,
                Cwd: cwd,
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        return store;
    }

    private static string CreateTempProjectDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-projectdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<JsonElement> RunPs(HarnessEffectExecutor exec, SessionState state, string toolId, string script)
    {
        var observed = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall(toolId, ToolSchemas.AgentShellExecute.Name, new { script }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        completed.Result.Should().BeOfType<JsonElement>();
        return (JsonElement)completed.Result;
    }

    private sealed class NullMeaiChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return YieldStop();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldStop()
            {
                await Task.CompletedTask;
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NullAcpClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new();

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unsupported method: {method}");
    }
}
