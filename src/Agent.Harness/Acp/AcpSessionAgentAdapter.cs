using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Harness.Acp;

/// <summary>
/// Minimal ACP adapter that bridges SessionCore events to ACP session/update streaming.
/// This is intentionally small; we will evolve it TDD-style.
/// </summary>
public sealed class AcpSessionAgentAdapter : IAcpSessionAgent
{
    private readonly SessionCore _core;
    private readonly IAcpSessionEvents _events;

    public AcpSessionAgentAdapter(SessionCore core, IAcpSessionEvents events)
    {
        _core = core;
        _events = events;
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // For the first slice, treat the prompt as a single user text message.
        var userText = ExtractUserText(request);

        var result = await _core.HandleUserMessageAsync(userText, cancellationToken).ConfigureAwait(false);

        // Streaming: emit assistant message chunk.
        await _events.SendSessionUpdateAsync(new AgentMessageChunk
        {
            Content = new TextContent { Text = result.AssistantText },
        }, cancellationToken).ConfigureAwait(false);

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

    private static string ExtractUserText(PromptRequest request)
    {
        // Keep this strict + minimal for now.
        // ACP PromptRequest supports content blocks; for our MVP test, we only accept one TextContent.
        var first = request.Prompt.FirstOrDefault();
        if (first is TextContent t)
            return t.Text;

        throw new InvalidOperationException("PromptRequest.content must contain a TextContent");
    }
}
