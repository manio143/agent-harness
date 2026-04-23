using System.Collections.Immutable;
using System.Diagnostics;

namespace Agent.Harness.Threads;

public sealed class ThreadManager : IThreadTools
{
    public bool HasAnyPendingInbox(string threadId)
    {
        var items = LoadPendingInbox(threadId);
        return !items.IsDefaultOrEmpty;
    }

    public bool HasDeliverableEnqueueNow(string threadId)
    {
        if (ProjectStatus(threadId) != ThreadStatus.Idle)
            return false;

        var items = LoadPendingInbox(threadId);
        return items.Any(e => ThreadInboxDeliveryText.IsEnqueue(e.Delivery));
    }

    public bool HasImmediateOrDeliverableEnqueue(string threadId)
    {
        var items = LoadPendingInbox(threadId);
        if (items.IsDefaultOrEmpty) return false;

        if (items.Any(i => ThreadInboxDeliveryText.IsImmediate(i.Delivery)))
            return true;

        return HasDeliverableEnqueueNow(threadId);
    }

    private ThreadStatus ProjectStatus(string threadId)
    {
        var committed = _store.LoadCommittedEvents(_sessionId, threadId);
        return ThreadStatusProjector.ProjectStatus(committed);
    }

    private readonly string _sessionId;
    private readonly IThreadStore _store;
    public ThreadManager(string sessionId, IThreadStore store)
    {
        _sessionId = sessionId;
        _store = store;
        _store.CreateMainIfMissing(sessionId);
    }



    public ImmutableArray<ThreadInfo> List()
    {
        return _store.ListThreads(_sessionId)
            .Where(m => string.IsNullOrWhiteSpace(m.ClosedAtIso))
            .Select(m => new ThreadInfo(
                ThreadId: m.ThreadId,
                ParentThreadId: m.ParentThreadId,
                Status: ProjectStatus(m.ThreadId),
                Mode: m.Mode,
                Intent: m.Intent,
                Model: string.IsNullOrWhiteSpace(m.Model) ? "default" : m.Model))
            .ToImmutableArray();
    }

    public ThreadMetadata? TryGetThreadMetadata(string threadId)
        => _store.TryLoadThreadMetadata(_sessionId, threadId);


    // Thread lifecycle (create/fork) is owned by ThreadOrchestrator in the unified model.
    // ThreadManager remains as a projector/utility facade over the thread store (list/read/inbox status).


    public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId)
    {
        var evts = _store.LoadCommittedEvents(_sessionId, threadId);

        // Fork read-window: only show messages from the fork point onward.
        // Fork point is defined as the first committed NewThreadTask marker in the child.
        var startIndex = 0;
        for (var i = 0; i < evts.Length; i++)
        {
            if (evts[i] is not NewThreadTask t) continue;
            if (t.IsFork) startIndex = i;
            break;
        }

        var list = new List<ThreadMessage>();
        var pendingAssistant = (string?)null;

        void FlushAssistant()
        {
            if (string.IsNullOrEmpty(pendingAssistant)) return;
            list.Add(new ThreadMessage("assistant", pendingAssistant));
            pendingAssistant = null;
        }

        foreach (var e in evts.Skip(startIndex))
        {
            switch (e)
            {
                case AssistantTextDelta d:
                    pendingAssistant = (pendingAssistant ?? string.Empty) + d.TextDelta;
                    break;

                case AssistantMessage a:
                    // If the runtime commits both streaming deltas and the final assistant message,
                    // prefer the final message to avoid duplicates.
                    pendingAssistant = null;
                    list.Add(new ThreadMessage("assistant", a.Text));
                    break;

                case UserMessage u:
                    FlushAssistant();
                    list.Add(new ThreadMessage("user", u.Text));
                    break;

                case InterThreadMessage it:
                    FlushAssistant();
                    list.Add(new ThreadMessage("inter_thread", it.Text));
                    break;

                case NewThreadTask t:
                    FlushAssistant();
                    list.Add(new ThreadMessage("system", NewThreadTaskMarkup.Render(t)));
                    break;

                default:
                    FlushAssistant();
                    break;
            }
        }

        FlushAssistant();
        return list.ToImmutableArray();

    }

    public void ReportIntent(string threadId, string intent)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta is null)
            throw new InvalidOperationException($"unknown_thread:{threadId}");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var next = meta with { Intent = intent, UpdatedAtIso = now };
        _store.SaveThreadMetadata(_sessionId, next);
    }

    public string GetModel(string threadId)
    {
        var meta = _store.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta is null)
            throw new InvalidOperationException($"unknown_thread:{threadId}");

        return string.IsNullOrWhiteSpace(meta.Model) ? "default" : meta.Model;
    }


    private ImmutableArray<ThreadInboxMessageEnqueued> LoadPendingInbox(string threadId)
    {
        var committed = _store.LoadCommittedEvents(_sessionId, threadId);

        var deliveredIds = committed
            .OfType<ThreadInboxMessageDequeued>()
            .Where(d => d.ThreadId == threadId)
            .Select(d => d.EnvelopeId)
            .ToHashSet(StringComparer.Ordinal);

        return committed
            .OfType<ThreadInboxMessageEnqueued>()
            .Where(e => e.ThreadId == threadId)
            .Where(e => !deliveredIds.Contains(e.EnvelopeId))
            .ToImmutableArray();
    }
}
