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
        => delivery switch
        {
            Enqueue => InboxDelivery.Enqueue,
            Immediate => InboxDelivery.Immediate,
            _ => InboxDelivery.Immediate,
        };
}
