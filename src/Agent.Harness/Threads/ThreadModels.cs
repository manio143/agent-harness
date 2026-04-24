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

public enum ThreadMode
{
    Multi,
    Single,
}

/// <summary>
/// Persisted thread metadata. Treated as a cache/projection artifact; the source of truth is the
/// committed thread event log (events.jsonl).
/// </summary>
public sealed record ThreadCapabilitiesSpec(
    ImmutableArray<string> Allow,
    ImmutableArray<string> Deny)
{
    public static ThreadCapabilitiesSpec Empty { get; } = new(
        Allow: ImmutableArray<string>.Empty,
        Deny: ImmutableArray<string>.Empty);
}

public sealed record ThreadMetadata(
    string ThreadId,
    string? ParentThreadId,
    string? Intent,
    string CreatedAtIso,
    string UpdatedAtIso,
    ThreadMode Mode,
    string? Model,
    int CompactionCount = 0,
    string? ClosedAtIso = null,
    string? ClosedReason = null,
    ThreadCapabilitiesSpec? Capabilities = null);

public enum ThreadInboxMessageKind
{
    UserPrompt = 0,
    InterThreadMessage = 1,
    ThreadIdleNotification = 2,
    NewThreadTask = 3,
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
    ThreadMode Mode,
    string? Intent,
    string Model);

public sealed record ThreadMessage(
    string Role,
    string Text);
