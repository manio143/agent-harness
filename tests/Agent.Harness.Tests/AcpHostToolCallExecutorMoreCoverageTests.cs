using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpHostToolCallExecutorMoreCoverageTests
{
    [Fact]
    public async Task ReadTextFile_WhenClientThrows_WrapsErrorWithNormalizedPath()
    {
        var fs = new InMemoryFs();
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true } }, fs) { ThrowOnRead = true };
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "read_text_file", new { path = "a.txt" }), CancellationToken.None);

        var err = obs.OfType<ObservedToolCallFailed>().Single().Error;
        err.Should().Contain("fs/read_text_file failed for path=");
        err.Should().Contain("/cwd/a.txt");
    }

    [Fact]
    public async Task ReadTextFile_supports_line_ranges_and_includes_metadata()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "l1\nl2\nl3\nl4\n");

        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var obs = await exec.ExecuteAsync(
            SessionState.Empty,
            new ExecuteToolCall("t1", "read_text_file", new { path = "a.txt", lines = new { from = 2, to = 3 } }),
            CancellationToken.None);

        var done = obs.OfType<ObservedToolCallCompleted>().Single();

        done.Result.Should().BeOfType<JsonElement>();
        var el = (JsonElement)done.Result;

        el.GetProperty("content").GetString().Should().Be("l2\nl3");
        el.GetProperty("total_lines").GetInt32().Should().Be(5); // trailing newline counts as final empty line
        el.GetProperty("lines_from").GetInt32().Should().Be(2);
        el.GetProperty("lines_to").GetInt32().Should().Be(3);
        el.GetProperty("is_partial").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PatchTextFile_WhenEditsMissing_FailsMissingRequiredEdits()
    {
        var fs = new InMemoryFs();
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt" }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("missing_required:edits");
    }

    [Fact]
    public async Task PatchTextFile_WhenReadThrows_WrapsError()
    {
        var fs = new InMemoryFs();
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs) { ThrowOnRead = true };
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var edits = new object[] { new { op = "delete_exact", text = "x" } };
        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("fs/read_text_file failed for path=");
    }

    [Fact]
    public async Task PatchTextFile_WhenWriteThrows_WrapsError()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x");

        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs) { ThrowOnWrite = true };
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var edits = new object[] { new { op = "delete_exact", text = "x" } };
        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("fs/write_text_file failed for path=");
    }

    [Fact]
    public async Task NormalizeFsPath_WhenRawPathIsWhitespace_PassesThrough()
    {
        var fs = new InMemoryFs();
        fs.Write(" ", "ok");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: null, store: null);

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "read_text_file", new { path = " " }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single();
        client.LastReadPath.Should().Be(" ");
    }

    [Fact]
    public async Task NormalizeFsPath_WhenCwdMissing_UsesGetFullPath()
    {
        var fs = new InMemoryFs();
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true } }, fs) { ReturnEmptyIfMissing = true };
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: null, store: null);

        await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "read_text_file", new { path = "a.txt" }), CancellationToken.None);

        client.LastReadPath.Should().Be(Path.GetFullPath("a.txt"));
    }

    [Fact]
    public async Task PatchTextFile_WhenExpectedShaIsNotHex_Fails()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var edits = new object[] { new { op = "delete_exact", text = "x" } };
        var badSha = new string('g', 64);

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", expectedSha256 = badSha, edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("expectedSha256_not_sha256");
    }

    [Fact]
    public async Task PatchTextFile_InvalidEditShapes_AreRejected()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x x");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        // edit_not_object
        {
            var edits = new object[] { "x" };
            var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);
            obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:edit_not_object");
        }

        // missing_required:op
        {
            var edits = new object[] { new { } };
            var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t2", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);
            obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:missing_required:op");
        }

        // unknown_op
        {
            var edits = new object[] { new { op = "wat" } };
            var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t3", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);
            obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:unknown_op:wat");
        }
    }

    [Fact]
    public async Task PatchTextFile_ReplaceExact_MissingOldText_IsRejected()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var edits = new object[] { new { op = "replace_exact", newText = "y" } };
        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:missing_required:oldText");
    }

    [Fact]
    public async Task PatchTextFile_ReplaceExact_OldTextEmpty_IsRejected()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var edits = new object[] { new { op = "replace_exact", oldText = "", newText = "y" } };
        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:oldText_empty");
    }

    [Fact]
    public async Task PatchTextFile_InsertBefore_AnchorTextEmpty_IsRejected()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var edits = new object[] { new { op = "insert_before", anchorText = "", text = "y" } };
        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:anchorText_empty");
    }

    [Fact]
    public async Task PatchTextFile_OccurrenceValidation_AndOccurrenceHappyPath_AreCovered()
    {
        var fs = new InMemoryFs();
        fs.Write("/cwd/a.txt", "x x x");
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        // occurrence must be positive
        {
            var edits = new object[] { new { op = "replace_exact", oldText = "x", newText = "y", occurrence = 0 } };
            var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);
            obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("invalid_args:occurrence_must_be_positive");
        }

        // not found when occurrence specified
        {
            var edits = new object[] { new { op = "replace_exact", oldText = "zzz", newText = "y", occurrence = 1 } };
            var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t2", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);
            obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("patch_failed:not_found");
        }

        // happy path with occurrence resolves correct index
        {
            var edits = new object[] { new { op = "replace_exact", oldText = "x", newText = "y", occurrence = 2 } };
            var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t3", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);
            obs.OfType<ObservedToolCallCompleted>().Single();
            fs.Read("/cwd/a.txt").Should().Be("x y x");
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolNameIsUnknown_ReturnsUnknownToolFailed()
    {
        var fs = new InMemoryFs();
        var client = new FakeFsClientCaller(new ClientCapabilities { Fs = new FileSystemCapabilities() }, fs);
        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new MetaStore("/cwd"));

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "nope", new { }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Be("unknown_tool");
    }

    private sealed class InMemoryFs
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public string Read(string path) => _files[path];
        public void Write(string path, string content) => _files[path] = content;
    }

    private sealed class MetaStore(string cwd) : ISessionStore
    {
        public void CreateNew(string sessionId, SessionMetadata metadata) { }
        public bool Exists(string sessionId) => true;
        public ImmutableArray<string> ListSessionIds() => ImmutableArray<string>.Empty;
        public SessionMetadata? TryLoadMetadata(string sessionId) => new(sessionId, cwd, null, "", "");
        public ImmutableArray<SessionEvent> LoadCommitted(string sessionId) => ImmutableArray<SessionEvent>.Empty;
        public void AppendCommitted(string sessionId, SessionEvent evt) { }
        public void UpdateMetadata(string sessionId, SessionMetadata metadata) { }
    }

    private sealed class FakeFsClientCaller(ClientCapabilities caps, InMemoryFs fs) : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public bool ThrowOnRead { get; init; }
        public bool ThrowOnWrite { get; init; }
        public bool ReturnEmptyIfMissing { get; init; }
        public string? LastReadPath { get; private set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            switch (method)
            {
                case "fs/read_text_file":
                {
                    if (ThrowOnRead)
                        throw new InvalidOperationException("boom");

                    var req = (ReadTextFileRequest)(object)request!;
                    LastReadPath = req.Path;

                    if (ReturnEmptyIfMissing && !fsReadExists(req.Path))
                        return Task.FromResult((TResponse)(object)new ReadTextFileResponse { Content = "" });

                    var resp = new ReadTextFileResponse { Content = fs.Read(req.Path) };
                    return Task.FromResult((TResponse)(object)resp);

                    bool fsReadExists(string path)
                    {
                        try { fs.Read(path); return true; }
                        catch { return false; }
                    }
                }

                case "fs/write_text_file":
                {
                    if (ThrowOnWrite)
                        throw new InvalidOperationException("boom");

                    var req = (WriteTextFileRequest)(object)request!;
                    fs.Write(req.Path, req.Content);
                    return Task.FromResult(default(TResponse)!);
                }

                default:
                    throw new NotSupportedException(method);
            }
        }
    }
}
