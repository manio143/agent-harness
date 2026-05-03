using Agent.Acp.Schema;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public abstract record ChatEvent;

public sealed record ChatSessionUpdate(string SessionId, SessionUpdate Update) : ChatEvent;

/// <summary>
/// Local user input (before/while sending to agent). Not part of ACP session/update stream.
/// </summary>
public sealed record ChatUserPrompt(string Text) : ChatEvent;
