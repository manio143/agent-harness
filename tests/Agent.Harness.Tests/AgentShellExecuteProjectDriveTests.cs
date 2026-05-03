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
    public async Task AgentShellExecute_ProjectDrive_UsesAcpForReadWrite()
    {
        var projectDir = CreateTempProjectDir();
        var (exec, state, fake) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path project:\\demo.txt -Value 'hello'; Get-Content -Path project:\\demo.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        res.GetProperty("stdout").GetString().Should().Be("hello");
        fake.LastWritePath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "demo.txt")));
        fake.LastReadPath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "demo.txt")));
        File.Exists(Path.Combine(projectDir, "demo.txt")).Should().BeFalse("content operations should stay ACP-backed");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_AllowsDriveRootedPaths()
    {
        var projectDir = CreateTempProjectDir();
        var (exec, state, fake) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path project:\\/dir/file.txt -Value 'x'; Get-Content -Path project:\\/dir/file.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        fake.LastWritePath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "dir", "file.txt")));
        fake.LastReadPath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "dir", "file.txt")));
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_NormalizesTraversalWithinDrive()
    {
        var projectDir = CreateTempProjectDir();
        var (exec, state, fake) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path project:\\a\\..\\secret.txt -Value 'y'; Get-Content -Path project:\\secret.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        fake.LastWritePath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "secret.txt")));
        fake.LastReadPath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "secret.txt")));
    }

    [Fact]
    public void ProjectDrivePsContext_RejectsPathsThatEscapeProjectRoot()
    {
        var projectDir = CreateTempProjectDir();
        var ctx = new Agent.Harness.Shell.ProjectDrivePsContext("s1", client: null, sessionCwd: projectDir, store: null);

        var act = () => ctx.NormalizeProjectRelativePath("../../etc/passwd");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("project_path_traversal_not_allowed:*");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_RejectsProviderQualifiedPaths()
    {
        var projectDir = CreateTempProjectDir();
        var (exec, state, _) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-Content -Path project:\\env:PATH");

        res.GetProperty("ok").GetBoolean().Should().BeFalse();
        res.GetProperty("stderr").GetString().Should().Contain("does not exist");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_AllowsFileListingOutsideAcp()
    {
        var projectDir = CreateTempProjectDir();
        var childDir = Path.Combine(projectDir, "src");
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(childDir, "b.txt"), "b");

        var (exec, state, fake) = Arrange(projectDir);

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-ChildItem -Path project:\\src | Sort-Object Name | Select-Object -ExpandProperty Name");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        res.GetProperty("stdout").GetString().Should().Be("a.txt\nb.txt");
        fake.LastReadPath.Should().BeNull();
        fake.LastWritePath.Should().BeNull();
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_WhenFsReadCapabilityMissing_ReturnsError()
    {
        var projectDir = CreateTempProjectDir();
        var store = CreateStore("s1", projectDir);

        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = null!,
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullChatClient(),
            store: store,
            sessionCwd: projectDir,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-Content -Path project:\\demo.txt");

        res.GetProperty("ok").GetBoolean().Should().BeFalse();
        res.GetProperty("stderr").GetString().Should().Contain("fs.readTextFile");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_WhenFsWriteCapabilityMissing_ReturnsError()
    {
        var projectDir = CreateTempProjectDir();
        var store = CreateStore("s1", projectDir);

        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = false },
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullChatClient(),
            store: store,
            sessionCwd: projectDir,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path project:\\demo.txt -Value 'x'");

        res.GetProperty("ok").GetBoolean().Should().BeFalse();
        res.GetProperty("stderr").GetString().Should().Contain("fs.writeTextFile");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_ListingWorksWithoutClientContext()
    {
        var projectDir = CreateTempProjectDir();
        File.WriteAllText(Path.Combine(projectDir, "demo.txt"), "demo");
        var store = CreateStore("s1", projectDir);

        var handler = new Agent.Harness.Tools.Handlers.AgentShellExecuteToolHandler(
            sessionId: "s1",
            threadId: Agent.Harness.Threads.ThreadIds.Main,
            store: store,
            client: null,
            sessionCwd: projectDir);

        var observed = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", ToolSchemas.AgentShellExecute.Name, new { script = "Get-ChildItem -Path project:\\ | Select-Object -ExpandProperty Name" }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        var json = (JsonElement)completed.Result;

        json.GetProperty("ok").GetBoolean().Should().BeTrue(json.GetProperty("stderr").GetString());
        json.GetProperty("stdout").GetString().Should().Contain("demo.txt");
    }

    [Fact]
    public async Task AgentShellExecute_ProjectDrive_UsesStoredSessionCwd_WhenSessionCwdNotPassed()
    {
        var projectDir = CreateTempProjectDir();
        File.WriteAllText(Path.Combine(projectDir, "from-store.txt"), "demo");
        var store = CreateStore("s1", projectDir);

        var handler = new Agent.Harness.Tools.Handlers.AgentShellExecuteToolHandler(
            sessionId: "s1",
            threadId: Agent.Harness.Threads.ThreadIds.Main,
            store: store,
            client: null,
            sessionCwd: null);

        var observed = await handler.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", ToolSchemas.AgentShellExecute.Name, new { script = "Get-ChildItem -Path project:\\ | Select-Object -ExpandProperty Name" }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        var json = (JsonElement)completed.Result;

        json.GetProperty("ok").GetBoolean().Should().BeTrue(json.GetProperty("stderr").GetString());
        json.GetProperty("stdout").GetString().Should().Contain("from-store.txt");
    }

    private static (HarnessEffectExecutor exec, SessionState state, FakeFsAcpClientCaller fake) Arrange(string cwd)
    {
        var store = CreateStore("s1", cwd);
        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullChatClient(),
            store: store,
            sessionCwd: cwd,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };
        return (exec, state, fake);
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

    private sealed class NullChatClient : IChatClient
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

    private sealed class FakeFsAcpClientCaller : IAcpClientCaller
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public FakeFsAcpClientCaller(ClientCapabilities caps) => ClientCapabilities = caps;

        public ClientCapabilities ClientCapabilities { get; }
        public string? LastReadPath { get; private set; }
        public string? LastWritePath { get; private set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            switch (method)
            {
                case "fs/read_text_file":
                {
                    var r = (ReadTextFileRequest)(object)request!;
                    LastReadPath = r.Path;
                    _files.TryGetValue(r.Path, out var content);
                    object resp = new ReadTextFileResponse { Content = content ?? string.Empty };
                    return Task.FromResult((TResponse)resp);
                }

                case "fs/write_text_file":
                {
                    var r = (WriteTextFileRequest)(object)request!;
                    LastWritePath = r.Path;
                    _files[r.Path] = r.Content;
                    return Task.FromResult(default(TResponse)!);
                }

                default:
                    throw new NotSupportedException($"Unsupported method: {method}");
            }
        }
    }
}
