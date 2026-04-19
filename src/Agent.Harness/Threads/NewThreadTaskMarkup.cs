namespace Agent.Harness.Threads;

public static class NewThreadTaskMarkup
{
    public static string Render(NewThreadTask t)
    {
        var created = $"<thread_created id=\"{t.ThreadId}\" parent_id=\"{t.ParentThreadId}\" />";
        var notice = t.IsFork
            ? "\n<notice>This is a forked thread with historical context that should be used when completing the task.</notice>"
            : "";
        var task = $"\n<task>{t.Message}</task>";
        return created + notice + task;
    }
}
