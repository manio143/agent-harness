namespace Agent.Harness.Threads;

public static class ThreadInboxDeliveryText
{
    public const string Enqueue = "enqueue";
    public const string Immediate = "immediate";

    public static string Serialize(InboxDelivery delivery)
        => delivery switch
        {
            InboxDelivery.Enqueue => Enqueue,
            InboxDelivery.Immediate => Immediate,
            _ => Immediate,
        };

    public static InboxDelivery Parse(string? delivery)
        => IsEnqueue(delivery) ? InboxDelivery.Enqueue : InboxDelivery.Immediate;

    public static string Normalize(string? delivery)
        => Serialize(Parse(delivery));

    public static bool IsImmediate(string? delivery)
        => string.Equals(delivery, Immediate, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnqueue(string? delivery)
        => string.Equals(delivery, Enqueue, StringComparison.OrdinalIgnoreCase);
}
