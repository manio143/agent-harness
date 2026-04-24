using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadSendToolHandler(
    IThreadTools? threadTools,
    IThreadObserver? observer,
    IThreadScheduler? scheduler,
    string currentThreadId) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_send",
        Description: "Send a message to another thread by enqueuing it in that thread's inbox.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Target thread id" },
            "message": { "type": "string", "description": "Message to enqueue" },
            "delivery": { "type": "string", "enum": ["enqueue", "immediate"], "description": "Whether the message should be delivered immediately or enqueued until idle" }
          },
          "required": ["threadId", "message"]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

        var threadId = GetRequiredString(args, "threadId");
        var message = GetRequiredString(args, "message");
        var delivery = ParseDelivery(args);

        if (threadId != currentThreadId)
        {
            if (threadTools is null)
                throw new InvalidOperationException("thread_tools_require_orchestrator");

            var meta = threadTools.TryGetThreadMetadata(threadId);
            if (meta is null)
                throw new InvalidOperationException($"unknown_thread:{threadId}");

            if (!string.IsNullOrWhiteSpace(meta.ClosedAtIso))
                throw new InvalidOperationException($"thread_closed:{threadId}");
        }

        var inboxArrived = ThreadInboxArrivals.InterThreadMessage(
            threadId: threadId,
            text: message,
            sourceThreadId: currentThreadId,
            source: "thread",
            delivery: delivery);

        // Special-case: sending to self should not require orchestrator. We surface the arrival
        // as a local observation so the core turn can commit it.
        if (threadId == currentThreadId)
        {
            return ImmutableArray.Create<ObservedChatEvent>(
                inboxArrived,
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
        }

        // Cross-thread requires orchestrator wiring.
        if (observer is null || scheduler is null)
            throw new InvalidOperationException("thread_tools_require_orchestrator");

        await observer.ObserveAsync(threadId, inboxArrived, cancellationToken).ConfigureAwait(false);

        if (delivery == InboxDelivery.Immediate)
            scheduler.ScheduleRun(threadId);

        return ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
    }

    private static InboxDelivery ParseDelivery(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("delivery", out var el) || el.ValueKind != JsonValueKind.String)
            return InboxDelivery.Immediate;

        var v = el.GetString();
        return ThreadInboxDeliveryText.Parse(v);
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
