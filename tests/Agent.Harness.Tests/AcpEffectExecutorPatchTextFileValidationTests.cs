using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorPatchTextFileValidationTests
{
    [Fact]
    public async Task PatchTextFile_InvalidExpectedSha256Format_IsInvalidArgs()
    {
        var client = new InMemoryFsClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        client.Files["/cwd/demo.txt"] = "hello world";

        var exec = new HarnessEffectExecutor(
            sessionId: "sess1",
            client: client,
            chat: new NullMeaiChatClient(),
            mcp: new NullMcpInvoker(),
            sessionCwd: "/cwd");

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.PatchTextFile) };

        var obs = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall(
                ToolId: "call_1",
                ToolName: "patch_text_file",
                Args: new
                {
                    path = "demo.txt",
                    expectedSha256 = "not-a-sha",
                    edits = new object[] { new { op = "replace_exact", oldText = "world", newText = "there" } },
                }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Should().ContainSingle();
        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("invalid_args").And.Contain("expectedSha256");

        client.Files["/cwd/demo.txt"].Should().Be("hello world");
    }

    [Fact]
    public async Task PatchTextFile_InsertAfter_EmptyText_IsInvalidArgs()
    {
        var client = new InMemoryFsClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        client.Files["/cwd/demo.txt"] = "hello";

        var exec = new HarnessEffectExecutor(
            sessionId: "sess1",
            client: client,
            chat: new NullMeaiChatClient(),
            mcp: new NullMcpInvoker(),
            sessionCwd: "/cwd");

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.PatchTextFile) };

        var obs = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall(
                ToolId: "call_1",
                ToolName: "patch_text_file",
                Args: new
                {
                    path = "demo.txt",
                    edits = new object[] { new { op = "insert_after", anchorText = "hello", text = "" } },
                }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Should().ContainSingle();
        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("invalid_args").And.Contain("text_empty");
        client.Files["/cwd/demo.txt"].Should().Be("hello");
    }

    [Fact]
    public async Task PatchTextFile_DeleteExact_EmptyText_IsInvalidArgs()
    {
        var client = new InMemoryFsClientCaller(new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
        });

        client.Files["/cwd/demo.txt"] = "hello";

        var exec = new HarnessEffectExecutor(
            sessionId: "sess1",
            client: client,
            chat: new NullMeaiChatClient(),
            mcp: new NullMcpInvoker(),
            sessionCwd: "/cwd");

        var state = SessionState.Empty with { Tools = ImmutableArray.Create(ToolSchemas.PatchTextFile) };

        var obs = await exec.ExecuteAsync(
            state,
            new ExecuteToolCall(
                ToolId: "call_1",
                ToolName: "patch_text_file",
                Args: new
                {
                    path = "demo.txt",
                    edits = new object[] { new { op = "delete_exact", text = "" } },
                }),
            CancellationToken.None);

        obs.OfType<ObservedToolCallFailed>().Should().ContainSingle();
        obs.OfType<ObservedToolCallFailed>().Single().Error.Should().Contain("invalid_args").And.Contain("text_empty");
        client.Files["/cwd/demo.txt"].Should().Be("hello");
    }

    private sealed class InMemoryFsClientCaller(ClientCapabilities caps) : IAcpClientCaller
    {
        public Dictionary<string, string> Files { get; } = new();

        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            object resp = method switch
            {
                "fs/read_text_file" => HandleRead((ReadTextFileRequest)(object)request!),
                "fs/write_text_file" => HandleWrite((WriteTextFileRequest)(object)request!),
                _ => throw new NotSupportedException(method),
            };

            return Task.FromResult((TResponse)resp);
        }

        private ReadTextFileResponse HandleRead(ReadTextFileRequest req)
        {
            if (!Files.TryGetValue(req.Path, out var content))
                throw new InvalidOperationException("not_found");
            return new ReadTextFileResponse { Content = content };
        }

        private object HandleWrite(WriteTextFileRequest req)
        {
            Files[req.Path] = req.Content;
            return new object();
        }
    }

    private sealed class NullMcpInvoker : IMcpToolInvoker
    {
        public bool CanInvoke(string toolName) => false;
        public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class NullMeaiChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
