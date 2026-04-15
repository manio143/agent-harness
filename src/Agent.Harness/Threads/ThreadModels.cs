using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public static class ThreadIds
{
    public const string Main = "main";
}

public enum ThreadStatus
{
    Running,
    Idle,
}

/// <summary>
/// Persisted thread metadata. Treated as a cache/projection artifact; the source of truth is the
/// committed thread event log (events.jsonl).
/// </summary>
public sealed record ThreadMetadata(
    string ThreadId,
    string? ParentThreadId,
    string? Intent,
    string CreatedAtIso,
    string UpdatedAtIso);

public enum ThreadInboxMessageKind
{
    UserPrompt = 0,
    InterThreadMessage = 1,
    ThreadIdleNotification = 2,
}


public enum InboxDelivery
{
    Enqueue,
    Immediate,
}

public sealed record ThreadInfo(
    string ThreadId,
    string? ParentThreadId,
    ThreadStatus Status,
    string? Intent);

public sealed record ThreadMessage(
    string Role,
    string Text);
