using System.Text.Json;
using Agent.Harness;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ObservedEventJsonTests
{
    [Theory]
    [MemberData(nameof(KnownEvents))]
    public void ToJsonl_EncodesKnownEventType(ObservedChatEvent e, string expectedType)
    {
        var json = ObservedEventJson.ToJsonl(e);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().Should().Be(expectedType);
    }

    public static IEnumerable<object[]> KnownEvents()
    {
        yield return new object[] { new ObservedTurnStarted("t1"), "obs_turn_started" };
        yield return new object[] { new ObservedTurnStabilized("t1"), "obs_turn_stabilized" };
        yield return new object[] { new ObservedWakeModel("t1"), "obs_wake_model" };
        yield return new object[] { new ObservedUserMessage("hi"), "obs_user_message" };
        yield return new object[] { new ObservedAssistantTextDelta("a"), "obs_assistant_text_delta" };
        yield return new object[] { new ObservedReasoningTextDelta("r"), "obs_reasoning_text_delta" };
        yield return new object[] { new ObservedReasoningMessageCompleted("stop"), "obs_reasoning_message_completed" };
        yield return new object[] { new ObservedAssistantMessageCompleted("stop"), "obs_assistant_message_completed" };
        yield return new object[] { new ObservedTokenUsage(1, 2, 3, "qwen2.5:3b"), "obs_token_usage" };

        yield return new object[]
        {
            new ObservedToolCallDetected("1", "read_text_file", new { path = "demo.txt", n = 1 }),
            "obs_tool_call_detected",
        };

        yield return new object[] { new ObservedPermissionApproved("1", "ok"), "obs_permission_approved" };
        yield return new object[] { new ObservedPermissionDenied("1", "no"), "obs_permission_denied" };

        yield return new object[] { new ObservedToolCallProgressUpdate("1", new { message = "p" }), "obs_tool_call_progress" };
        yield return new object[] { new ObservedToolCallCompleted("1", new { ok = true }), "obs_tool_call_completed" };
        yield return new object[] { new ObservedToolCallFailed("1", "boom"), "obs_tool_call_failed" };
        yield return new object[] { new ObservedToolCallCancelled("1"), "obs_tool_call_cancelled" };

        yield return new object[] { new ObservedMcpConnectionFailed("srv", "err"), "obs_mcp_connection_failed" };
    }

    [Fact]
    public void ToJsonl_EncodesTokenUsageProviderModel()
    {
        var json = ObservedEventJson.ToJsonl(new ObservedTokenUsage(1, 2, 3, "qwen2.5:3b"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("providerModel").GetString().Should().Be("qwen2.5:3b");
    }

    [Fact]
    public void ToJsonl_EncodesToolCallArgsAsJsonElement()
    {
        var json = ObservedEventJson.ToJsonl(new ObservedToolCallDetected(
            ToolId: "1",
            ToolName: "read_text_file",
            Args: new { path = "demo.txt", n = 1 }));

        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("args");
        args.GetProperty("path").GetString().Should().Be("demo.txt");
        args.GetProperty("n").GetInt32().Should().Be(1);
    }

    [Fact]
    public void ToJsonl_WhenRawUpdatePresent_WrapsWithRawType()
    {
        var e = new ObservedWakeModel("t1")
        {
            RawUpdate = 123,
        };

        var json = ObservedEventJson.ToJsonl(e);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("rawType").GetString().Should().NotBeNullOrWhiteSpace();
        var inner = doc.RootElement.GetProperty("event");
        inner.GetProperty("type").GetString().Should().Be("obs_wake_model");
        inner.GetProperty("threadId").GetString().Should().Be("t1");
    }

    [Fact]
    public void ToJsonl_WhenUnknownEvent_EmitsUnknownWithKind()
    {
        var json = ObservedEventJson.ToJsonl(new UnknownObserved());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().Should().Be("obs_unknown");
        doc.RootElement.GetProperty("kind").GetString().Should().Be(nameof(UnknownObserved));
    }

    private sealed record UnknownObserved : ObservedChatEvent;
}
