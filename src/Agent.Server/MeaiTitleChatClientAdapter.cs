using Agent.Harness;
using Microsoft.Extensions.AI;

namespace Agent.Server;

/// <summary>
/// Adapts MEAI IChatClient (OpenAI-compatible) to the harness IChatClient abstraction for title generation.
/// </summary>
public sealed class MeaiTitleChatClientAdapter : Agent.Harness.IChatClient
{
    private readonly Microsoft.Extensions.AI.IChatClient _chat;

    public MeaiTitleChatClientAdapter(Microsoft.Extensions.AI.IChatClient chat)
    {
        _chat = chat;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<Agent.Harness.ChatMessage> renderedMessages, CancellationToken cancellationToken)
    {
        var meai = renderedMessages
            .Select(m => new Microsoft.Extensions.AI.ChatMessage(m.Role switch
            {
                ChatRole.System => Microsoft.Extensions.AI.ChatRole.System,
                ChatRole.User => Microsoft.Extensions.AI.ChatRole.User,
                _ => Microsoft.Extensions.AI.ChatRole.Assistant,
            }, m.Text))
            .ToList();

        var resp = await _chat.GetResponseAsync(meai, cancellationToken: cancellationToken).ConfigureAwait(false);
        var last = resp.Messages.LastOrDefault();

        return resp.Text
            ?? last?.Text
            ?? last?.Contents?.OfType<TextContent>().FirstOrDefault()?.Text
            ?? string.Empty;
    }
}
