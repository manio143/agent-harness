using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class HarnessEffectExecutorToolResultCappingIntegrationTests
{
    [Fact]
    public async Task ExecuteToolCall_WhenResultIsTruncated_WritesRawToolResultFile_ForNonReadTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "toolcap", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("sess1", new SessionMetadata("sess1", "/cwd", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

            var caps = new ClientCapabilities { Terminal = true };
            var client = new CapturingTerminalClientCaller(caps)
            {
                OutputResponse = new TerminalOutputResponse
                {
                    ExitStatus = new ExitStatus(),
                    Output = new string('x', 200),
                    Truncated = false,
                },
            };

            var exec = new HarnessEffectExecutor(
                sessionId: "sess1",
                client: client,
                chat: new NullChatClient(),
                store: store,
                toolResultCapping: new Agent.Harness.Llm.ToolResultCappingOptions
                {
                    Enabled = true,
                    MaxStringChars = 10,
                });

            var state = SessionState.Empty with
            {
                Tools = ImmutableArray.Create(ToolSchemas.ExecuteCommand),
            };

            var obs = await exec.ExecuteAsync(
                state,
                new ExecuteToolCall("call_1", "execute_command", new { command = "echo", args = new[] { "hi" } }),
                CancellationToken.None);

            var done = obs.OfType<ObservedToolCallCompleted>().Single();
            done.Result.Should().BeOfType<Dictionary<string, object?>>();

            var dict = (Dictionary<string, object?>)done.Result;
            dict["_truncated"].Should().Be(true);
            dict.Should().ContainKey("_raw_result_file");

            var rawPath = dict["_raw_result_file"]!.ToString();
            File.Exists(rawPath).Should().BeTrue();
            File.ReadAllText(rawPath!).Should().Contain(new string('x', 50));
    }

    [Fact]
    public async Task ExecuteToolCall_read_text_file_WhenTruncated_IncludesTotalLinesFromOriginalFileNotJustShownContent()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"l{i}");
        var file = string.Join("\n", lines); // no trailing newline => total_lines==10

        var caps = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true } };
        var client = new FsClientCaller(caps, file);

        var exec = new HarnessEffectExecutor(
            sessionId: "sess1",
            client: client,
            chat: new NullChatClient(),
            toolResultCapping: new Agent.Harness.Llm.ToolResultCappingOptions
            {
                Enabled = true,
                MaxObjectProperties = 1,
            });

            var state = SessionState.Empty with
            {
                Tools = ImmutableArray.Create(ToolSchemas.ReadTextFile),
            };

            var obs = await exec.ExecuteAsync(
                state,
                new ExecuteToolCall("call_1", "read_text_file", new { path = "a.txt", lines = new { from = 2, to = 3 } }),
                CancellationToken.None);

            var done = obs.OfType<ObservedToolCallCompleted>().Single();
            done.Result.Should().BeOfType<Dictionary<string, object?>>();

            var dict = (Dictionary<string, object?>)done.Result;
            dict["_truncated"].Should().Be(true);

            // Must reflect the total file lines (10), not just the shown slice (2 lines).
            dict["total_lines"].Should().Be(10);
    }

    private sealed class CapturingTerminalClientCaller(ClientCapabilities caps) : IAcpClientCaller
    {
        public TerminalOutputResponse OutputResponse { get; init; } = new() { ExitStatus = new ExitStatus(), Output = "", Truncated = false };

        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            object resp = method switch
            {
                "terminal/create" => new CreateTerminalResponse { TerminalId = "term_1" },
                "terminal/wait_for_exit" => new WaitForTerminalExitResponse { ExitCode = 0 },
                "terminal/output" => OutputResponse,
                _ => throw new NotSupportedException($"Unsupported method: {method}"),
            };

            return Task.FromResult((TResponse)resp);
        }
    }

    private sealed class FsClientCaller(ClientCapabilities caps, string content) : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities => caps;

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            if (method != "fs/read_text_file")
                throw new NotSupportedException($"Unsupported method: {method}");

            var resp = new ReadTextFileResponse { Content = content };
            return Task.FromResult((TResponse)(object)resp);
        }
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
