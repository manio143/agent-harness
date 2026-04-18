using Microsoft.Extensions.AI;
using System.Linq;

namespace Agent.Harness.TitleGeneration;

/// <summary>
/// Imperative-shell component that can generate and commit a session title as a committed event.
///
/// Metadata is a projection of committed events (e.g. JsonlSessionStore projects SessionTitleSet into session.json).
/// </summary>
public sealed class SessionTitleGenerator
{
    public static SessionTitleGenerator Null { get; } = new NullSessionTitleGenerator();

    public const string SystemPrompt =
        "You're a title generator based on the following conversation <conversation>...</conversation> you must output precisely one short line that contains a title for this conversation.";

    private readonly IChatClient _chat;

    public SessionTitleGenerator(IChatClient chat)
    {
        _chat = chat;
    }

    public virtual async Task<SessionTitleSet?> MaybeGenerateAfterTurnAsync(SessionState state, CancellationToken cancellationToken)
    {
        if (state.Committed.Any(e => e is SessionTitleSet))
            return null;

        // Only generate after we have at least one assistant message committed.
        if (!state.Committed.Any(e => e is AssistantMessage))
            return null;

        var conversation = Core.RenderPrompt(state)
            .Select(m => $"{m.Role}: {m.Text}")
            .ToList();

        var user = "<conversation>\n" + string.Join("\n", conversation) + "\n</conversation>";

        var resp = await _chat.GetResponseAsync(
            new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, SystemPrompt),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, user),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var titleRaw = resp.Text;
        if (string.IsNullOrWhiteSpace(titleRaw))
            return null;

        var line = titleRaw.Trim().Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Small clamp.
        if (line.Length > 80) line = line[..80];

        return new SessionTitleSet(line);
    }
}

file sealed class NullSessionTitleGenerator : SessionTitleGenerator
{
    private sealed class NullChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(Array.Empty<ChatMessage>()));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    public NullSessionTitleGenerator() : base(new NullChatClient())
    {
    }

    public override Task<SessionTitleSet?> MaybeGenerateAfterTurnAsync(SessionState state, CancellationToken cancellationToken)
        => Task.FromResult<SessionTitleSet?>(null);
}
