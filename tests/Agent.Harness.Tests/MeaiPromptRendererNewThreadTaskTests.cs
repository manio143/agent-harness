using System.Collections.Immutable;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class MeaiPromptRendererNewThreadTaskTests
{
    [Fact]
    public void Render_NewThreadTask_NotFork_RendersThreadCreatedAndTaskOnly()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new NewThreadTask(ThreadId: "thr_new", ParentThreadId: "main", IsFork: false, Message: "do work"))
        };

        var msgs = MeaiPromptRenderer.Render(state);

        var sys = msgs.Single(m => m.Text.Contains("<thread_created", StringComparison.Ordinal));
        sys.Role.ToString().ToLowerInvariant().Should().Be("system");
        sys.Text.Should().Contain("<thread_created id=\"thr_new\" parent_id=\"main\" />");
        sys.Text.Should().Contain("<task>do work</task>");
        sys.Text.Should().NotContain("<notice>");
    }

    [Fact]
    public void Render_NewThreadTask_Fork_RendersNoticeBeforeTask()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new NewThreadTask(ThreadId: "thr_new", ParentThreadId: "main", IsFork: true, Message: "do work"))
        };

        var msgs = MeaiPromptRenderer.Render(state);

        var sys = msgs.Single(m => m.Text.Contains("<thread_created", StringComparison.Ordinal));
        sys.Role.ToString().ToLowerInvariant().Should().Be("system");
        sys.Text.Should().Contain("<thread_created id=\"thr_new\" parent_id=\"main\" />");
        sys.Text.Should().Contain("<notice>This is a forked thread with historical context that should be used when completing the task.</notice>");
        sys.Text.Should().Contain("<task>do work</task>");

        sys.Text.IndexOf("<notice>", StringComparison.Ordinal).Should().BeGreaterThan(sys.Text.IndexOf("<thread_created", StringComparison.Ordinal));
        sys.Text.IndexOf("<task>", StringComparison.Ordinal).Should().BeGreaterThan(sys.Text.IndexOf("<notice>", StringComparison.Ordinal));
    }
}
