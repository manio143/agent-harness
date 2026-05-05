using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;

namespace Agent.Acp.Tests;

public sealed class AcpToolCallRawInputTests
{
    [Fact]
    public async Task ToolCallRequested_publishes_rawInput_so_clients_can_render_parameters()
    {
        // This test captures a UX invariant from the Avalonia ACP client:
        // tool rows should show the actual tool name AND its parameters.
        // Parameters come from ToolCallRequested.Args and must be published to clients via rawInput.
        //
        // RED (current): ToolCallRequested only starts the tool call, but does not emit rawInput.

        var sent = new List<object>();
        var events = new CapturingEvents(sent);

        var tracker = new AcpToolCallTracker(events);
        var turn = new Turn(tracker);

        var publisher = new AcpCommittedEventPublisher(
            events,
            coreOptions: new CoreOptions { CommitAssistantTextDeltas = true, CommitReasoningTextDeltas = true },
            publishOptions: new AcpPublishOptions { PublishReasoning = true });

        var toolCalls = new Dictionary<string, IAcpToolCall>();

        var args = JsonSerializer.SerializeToElement(new { cmd = "echo", args = new[] { "hi" } });
        var committed = new ToolCallRequested(ToolId: "call_1", ToolName: "host.exec", Args: args);

        await publisher.PublishAsync(committed, turn, toolCalls, CancellationToken.None);

        // Assert: some session/update includes rawInput with our args.
        // We inspect the anonymous payloads sent by AcpToolCallTracker / publisher.
        var anyRawInput = sent
            .Select(o => JsonSerializer.SerializeToElement(o))
            .Any(e =>
            {
                if (!e.TryGetProperty("toolCallId", out var id) || id.GetString() != "call_1") return false;
                if (!e.TryGetProperty("rawInput", out var rawInput)) return false;
                if (rawInput.ValueKind == JsonValueKind.Null) return false;

                // loose check: ensure cmd=="echo" exists somewhere
                var raw = rawInput.GetRawText();
                return raw.Contains("\"cmd\":\"echo\"", StringComparison.Ordinal);
            });

        Assert.True(anyRawInput, "Expected a tool_call/tool_call_update payload containing rawInput for toolCallId=call_1");
    }

    private sealed class CapturingEvents : IAcpSessionEvents
    {
        private readonly List<object> _sent;

        public CapturingEvents(List<object> sent) => _sent = sent;

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            _sent.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class Turn : IAcpPromptTurn
    {
        public Turn(IAcpToolCalls toolCalls) => ToolCalls = toolCalls;
        public IAcpToolCalls ToolCalls { get; }
    }
}
