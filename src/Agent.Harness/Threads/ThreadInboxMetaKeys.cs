namespace Agent.Harness.Threads;

public static class ThreadInboxMetaKeys
{
    // NewThreadTask
    public const string ParentThreadId = "parentThreadId";
    public const string IsFork = "isFork";

    // ThreadIdleNotification
    public const string ChildThreadId = "childThreadId";
    public const string LastIntent = "lastIntent";

    // Forward-compat diagnostics
    public const string UnknownInboxKind = "unknownInboxKind";
}
