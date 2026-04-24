using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadStartToolHandler(
    IThreadTools? threadTools,
    IThreadLifecycle? lifecycle,
    IThreadObserver? observer,
    IThreadScheduler? scheduler,
    IThreadIdAllocator? threadIdAllocator,
    Func<string, bool>? isKnownModel,
    string currentThreadId) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_start",
        Description: "Start a child thread (new or fork from current thread) and attach an initial message. Optional: set the child's model.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Mandatory unique name/id for the new child thread (unique within the session). Use a short, stable identifier like 'research' or 'fix_deadlock'." },
            "context": { "type": "string", "enum": ["new", "fork"], "description": "Whether to start from empty state or fork the current thread" },
            "mode": { "type": "string", "enum": ["multi", "single"], "description": "Thread mode. multi: long-lived; can accept multiple tasks/messages. single: one-shot; thread is closed when it becomes idle with an empty inbox." },
            "message": { "type": "string", "description": "Initial message to attach to the child thread" },
            "delivery": { "type": "string", "enum": ["enqueue", "immediate"], "description": "Whether the message should be delivered immediately or enqueued until idle" },
            "model": { "type": "string", "description": "Optional model friendly name for the child thread (or 'default')" },
            "capabilities": {
              "type": "object",
              "properties": {
                "allow": { "type": "array", "items": { "type": "string" }, "description": "Capability allow selectors (e.g. 'fs.read', 'threads', 'mcp:*', 'mcp:everything', '*')" },
                "deny": { "type": "array", "items": { "type": "string" }, "description": "Capability deny selectors (deny wins)" }
              }
            }
          },
          "required": ["name", "context", "mode", "message"]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

        var name = GetRequiredString(args, "name");
        var context = GetRequiredString(args, "context");
        var modeText = GetRequiredString(args, "mode");
        var message = GetRequiredString(args, "message");
        var delivery = ParseDelivery(args);

        var mode = modeText switch
        {
            "multi" => ThreadMode.Multi,
            "single" => ThreadMode.Single,
            _ => throw new InvalidOperationException("thread_start.invalid_mode"),
        };

        if (!IsValidThreadName(name))
            throw new InvalidOperationException("thread_start.invalid_name");

        if (string.Equals(name, ThreadIds.Main, StringComparison.Ordinal))
            throw new InvalidOperationException("thread_start.name_reserved");

        if (threadTools is null || lifecycle is null || observer is null || scheduler is null || threadIdAllocator is null)
            throw new InvalidOperationException("thread_tools_require_orchestrator");

        // Keep the model's mental model simple: "name" is a human prefix and must be unique among *open* threads.
        // The actual thread id returned is "{name}-{hhhh}".
        if (threadTools.List().Any(t => t.ThreadId.StartsWith(name + "-", StringComparison.Ordinal)))
            throw new InvalidOperationException($"thread_already_exists:{name}");

        ImmutableArray<SessionEvent> seed = context switch
        {
            "new" => ImmutableArray<SessionEvent>.Empty,
            "fork" => ForkSeedBuilder.BuildForkSeed(state.Committed),
            _ => throw new InvalidOperationException("thread_start.invalid_context"),
        };

        var id = threadIdAllocator.AllocateThreadId(name);

        var capabilities = TryParseCapabilities(args);

        await lifecycle.RequestForkChildThreadAsync(
            currentThreadId,
            id,
            mode,
            seed,
            capabilities,
            cancellationToken).ConfigureAwait(false);

        if (args.TryGetValue("model", out var modelVal) && modelVal.ValueKind == JsonValueKind.String)
        {
            var model = modelVal.GetString()!;
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("thread_start.model_required");

            if (!string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) && isKnownModel is not null && !isKnownModel(model))
                model = "default";

            await observer.ObserveAsync(id, new ObservedSetModel(id, model), cancellationToken).ConfigureAwait(false);
        }

        await observer.ObserveAsync(
            id,
            ThreadInboxArrivals.NewThreadTask(
                threadId: id,
                parentThreadId: currentThreadId,
                isFork: string.Equals(context, "fork", StringComparison.Ordinal),
                message: message,
                delivery: delivery),
            cancellationToken).ConfigureAwait(false);

        if (delivery == InboxDelivery.Immediate)
            scheduler.ScheduleRun(id);

        return ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { threadId = id })));
    }

    private static ThreadCapabilitiesSpec? TryParseCapabilities(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
            return null;

        static bool IsValidSelector(string s)
        {
            if (s is "*" or "threads" or "fs.read" or "fs.write" or "host.exec" or "mcp:*")
                return true;

            if (!s.StartsWith("mcp:", StringComparison.Ordinal))
                return false;

            var rest = s[4..];
            if (rest.Length == 0)
                return false;

            if (rest == "*")
                return true;

            static bool IsValidId(string id)
            {
                if (id.Length == 0) return false;
                foreach (var ch in id)
                {
                    if (char.IsLetterOrDigit(ch)) continue;
                    if (ch is '_' or '-') continue;
                    return false;
                }
                return true;
            }

            var colon = rest.IndexOf(':');
            if (colon < 0)
            {
                // mcp:<server>
                return IsValidId(rest);
            }

            // mcp:<server>:<tool> or mcp:<server>:*
            var server = rest[..colon];
            var tool = rest[(colon + 1)..];

            if (!IsValidId(server))
                return false;

            if (tool == "*")
                return true;

            return IsValidId(tool);
        }

        ImmutableArray<string> ReadList(string name)
        {
            if (!caps.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
                return ImmutableArray<string>.Empty;

            var b = ImmutableArray.CreateBuilder<string>();
            foreach (var v in el.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.String) continue;
                var raw = v.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var s = raw.Trim();
                if (!IsValidSelector(s))
                    throw new InvalidOperationException($"thread_start.invalid_capability_selector:{s}");

                b.Add(s);
            }
            return b.ToImmutable();
        }

        return new ThreadCapabilitiesSpec(
            Allow: ReadList("allow"),
            Deny: ReadList("deny"));
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

    private static bool IsValidThreadName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 64) return false;

        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '_' or '-') continue;
            return false;
        }

        return true;
    }

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
