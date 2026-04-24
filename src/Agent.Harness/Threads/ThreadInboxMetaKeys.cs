namespace Agent.Harness.Threads;

public static class ThreadInboxMetaKeys
{
    // NewThreadTask
    public const string ParentThreadId = "parentThreadId";
    public const string IsFork = "isFork";

    // ThreadIdleNotification
    public const string ChildThreadId = "childThreadId";
    public const string LastIntent = "lastIntent";
    public const string LastAssistantMessage = "lastAssistantMessage";
    public const string ClosedReason = "closedReason";

    // Forward-compat diagnostics
    public const string UnknownInboxKind = "unknownInboxKind";
}
