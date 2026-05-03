using Agent.Acp.Schema;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public abstract record ChatEvent;

public sealed record ChatSessionUpdate(SessionUpdate Update) : ChatEvent;
