using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class AgentShellExecuteClientDriveTests
{
    [Fact]
    public async Task AgentShellExecute_ClientDrive_AllowsDriveRootedPaths()
    {
        var (exec, state, fake) = Arrange(cwd: "/repo/cwd");

        // Leading slash is treated as drive-root-relative, not host absolute.
        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path client:\\/dir/file.txt -Value 'x'; Get-Content -Path client:\\/dir/file.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        fake.LastWritePath.Should().Be(Path.GetFullPath("/repo/cwd/dir/file.txt"));
        fake.LastReadPath.Should().Be(Path.GetFullPath("/repo/cwd/dir/file.txt"));
    }

    [Fact]
    public async Task AgentShellExecute_ClientDrive_NormalizesTraversalWithinDrive()
    {
        var (exec, state, fake) = Arrange(cwd: "/repo/cwd");

        // PowerShell / provider may normalize the path; end result should be rooted under cwd.
        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path client:\\a\\..\\secret.txt -Value 'y'; Get-Content -Path client:\\secret.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        fake.LastWritePath.Should().Be(Path.GetFullPath("/repo/cwd/secret.txt"));
        fake.LastReadPath.Should().Be(Path.GetFullPath("/repo/cwd/secret.txt"));
    }

    [Fact]
    public async Task AgentShellExecute_ClientDrive_RejectsProviderQualifiedPaths()
    {
        var (exec, state, _) = Arrange(cwd: "/repo/cwd");

        // Provider-qualified inside client: should be rejected (contains ':').
        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-Content -Path client:\\env:PATH");

        res.GetProperty("ok").GetBoolean().Should().BeFalse();
        res.GetProperty("stderr").GetString().Should().Contain("client_path_must_be_relative");
    }

    [Fact]
    public async Task AgentShellExecute_ClientDrive_WhenFsCapabilityMissing_ReturnsError()
    {
        var cwd = "/repo/cwd";
        var storeRoot = Path.Combine(Path.GetTempPath(), "harness-clientdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var store = new JsonlSessionStore(storeRoot);
        store.CreateNew(
            sessionId: "s1",
            metadata: new SessionMetadata(
                SessionId: "s1",
                Cwd: cwd,
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            // No fs caps advertised.
            Fs = null,
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullMeaiChatClient(),
            store: store,
            sessionCwd: cwd,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };

        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-Content -Path client:\\demo.txt");

        res.GetProperty("ok").GetBoolean().Should().BeFalse();
        res.GetProperty("stderr").GetString().Should().Contain("fs.readTextFile");
    }

    [Fact]
    public async Task AgentShellExecute_WithoutClientContext_ClientDriveIsUnavailable()
    {
        // No ACP client injected into the shell session.
        var storeRoot = Path.Combine(Path.GetTempPath(), "harness-clientdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var store = new JsonlSessionStore(storeRoot);
        store.CreateNew(
            sessionId: "s1",
            metadata: new SessionMetadata(
                SessionId: "s1",
                Cwd: "/repo/cwd",
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        // Use the same fake client for HarnessEffectExecutor, but construct an AgentShellExecuteToolHandler directly
        // WITHOUT passing the client. This ensures the shell can't create client: drive.
        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        var handler = new Agent.Harness.Tools.Handlers.AgentShellExecuteToolHandler(
            sessionId: "s1",
            threadId: Agent.Harness.Threads.ThreadIds.Main,
            store: store,
            client: null,
            sessionCwd: "/repo/cwd");

        var state = SessionState.Empty;

        var observed = await handler.ExecuteAsync(
            state,
            new ExecuteToolCall("t1", ToolSchemas.AgentShellExecute.Name, new { script = "Get-PSDrive -Name client" }),
            CancellationToken.None);

        var completed = observed.OfType<ObservedToolCallCompleted>().Should().ContainSingle().Subject;
        var json = (JsonElement)completed.Result;

        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.GetProperty("stderr").GetString().Should().Contain("Cannot find drive");
    }

    private static (HarnessEffectExecutor exec, SessionState state, FakeFsAcpClientCaller fake) Arrange(string cwd)
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "harness-clientdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var store = new JsonlSessionStore(storeRoot);
        store.CreateNew(
            sessionId: "s1",
            metadata: new SessionMetadata(
                SessionId: "s1",
                Cwd: cwd,
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullMeaiChatClient(),
            store: store,
            sessionCwd: cwd,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };
        return (exec, state, fake);
    }

    [Fact]
    public async Task AgentShellExecute_ClientDrive_AllowsRelativeOnly_AndMapsToAcpFs()
    {
        var cwd = "/repo/cwd";
        var storeRoot = Path.Combine(Path.GetTempPath(), "harness-clientdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var store = new JsonlSessionStore(storeRoot);
        store.CreateNew(
            sessionId: "s1",
            metadata: new SessionMetadata(
                SessionId: "s1",
                Cwd: cwd,
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullMeaiChatClient(),
            store: store,
            sessionCwd: cwd,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };

        // Write then read using client: drive.
        var res = await RunPs(exec, state, toolId: "t1",
            script: "Set-Content -Path client:\\demo.txt -Value 'hello'; Get-Content -Path client:\\demo.txt");

        res.GetProperty("ok").GetBoolean().Should().BeTrue(res.GetProperty("stderr").GetString());
        res.GetProperty("stdout").GetString().Should().Be("hello", res.GetProperty("stderr").GetString());

        // Ensure ACP was called with a normalized path under the session cwd.
        fake.LastWritePath.Should().Be(Path.GetFullPath(Path.Combine(cwd, "demo.txt")));
        fake.LastReadPath.Should().Be(Path.GetFullPath(Path.Combine(cwd, "demo.txt")));
    }

    [Fact]
    public async Task AgentShellExecute_ClientDrive_RejectsEmbeddedDriveRoots()
    {
        var cwd = "/repo/cwd";
        var storeRoot = Path.Combine(Path.GetTempPath(), "harness-clientdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var store = new JsonlSessionStore(storeRoot);
        store.CreateNew(
            sessionId: "s1",
            metadata: new SessionMetadata(
                SessionId: "s1",
                Cwd: cwd,
                Title: null,
                CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var fake = new FakeFsAcpClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        var exec = new HarnessEffectExecutor(
            sessionId: "s1",
            client: fake,
            chat: new NullMeaiChatClient(),
            store: store,
            sessionCwd: cwd,
            threadId: Agent.Harness.Threads.ThreadIds.Main);

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.AgentShellExecute) };

        // This should be rejected: client:\C:\x.txt (embedded drive root)
        var res = await RunPs(exec, state, toolId: "t1",
            script: "Get-Content -Path client:\\C:\\x.txt");

        res.GetProperty("ok").GetBoolean().Should().BeFalse();
        res.GetProperty("stderr").GetString().Should().Contain("client_path_must_be_relative");
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

    private sealed class NullMeaiChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

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
