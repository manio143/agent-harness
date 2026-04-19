using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpHostToolCallExecutorCoverageTests
{
    [Fact]
    public async Task PatchTextFile_supports_insert_before_and_after_and_delete_exact()
    {
        var fs = new InMemoryFs();
        var caps = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } };
        var client = new FakeAcpClientCaller(caps, fs);

        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new InMemorySessionStore());

        fs.Write("/cwd/a.txt", "hello world");

        var edits = new object[]
        {
            new { op = "insert_before", anchorText = "world", text = "big " },
            new { op = "insert_after", anchorText = "hello", text = "," },
            new { op = "delete_exact", text = "," },
        };

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallCompleted>().Single();
        fs.Read("/cwd/a.txt").Should().Be("hello big world");
    }

    [Fact]
    public async Task PatchTextFile_whenMultipleMatchesAndNoOccurrence_Fails()
    {
        var fs = new InMemoryFs();
        var caps = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } };
        var client = new FakeAcpClientCaller(caps, fs);

        var exec = new AcpHostToolCallExecutor(sessionId: "s1", client: client, sessionCwd: "/cwd", store: new InMemorySessionStore());

        fs.Write("/cwd/a.txt", "x x x");

        var edits = new object[] { new { op = "replace_exact", oldText = "x", newText = "y" } };

        var obs = await exec.ExecuteAsync(SessionState.Empty, new ExecuteToolCall("t1", "patch_text_file", new { path = "a.txt", edits }), CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("multiple_matches");
    }

    private sealed class InMemoryFs
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public string Read(string path) => _files[path];
        public void Write(string path, string content) => _files[path] = content;
        public bool Exists(string path) => _files.ContainsKey(path);
    }

    private sealed class FakeAcpClientCaller(ClientCapabilities caps, InMemoryFs fs) : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            switch (method)
            {
                case "fs/read_text_file":
                {
                    var req = (ReadTextFileRequest)(object)request!;
                    var resp = new ReadTextFileResponse { Content = fs.Read(req.Path) };
                    return Task.FromResult((TResponse)(object)resp);
                }
                case "fs/write_text_file":
                {
                    var req = (WriteTextFileRequest)(object)request!;
                    fs.Write(req.Path, req.Content);
                    return Task.FromResult(default(TResponse)!);
                }
                default:
                    throw new NotSupportedException(method);
            }
        }
    }

    private sealed class InMemorySessionStore : ISessionStore
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
