using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests.Llm;

public sealed class MeaiPromptRendererTests
{
    [Fact]
    public void Render_WhenToolCallAndResultPresent_UsesAssistantFunctionCallAndToolFunctionResult()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/tmp/a.txt" });
        var result = JsonSerializer.SerializeToElement(new { ok = true });

        var state = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("do it"),
                new ToolCallRequested("call_1", "read_text_file", args),
                new ToolCallCompleted("call_1", result),
                new AssistantMessage("done")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray<ToolDefinition>.Empty);

        var rendered = MeaiPromptRenderer.Render(state);

        rendered[0].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.User);
        rendered[0].Text.Should().Be("do it");

        rendered[1].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.Assistant);
        rendered[1].Contents.Should().ContainSingle(c => c is FunctionCallContent);
        var fc = (FunctionCallContent)rendered[1].Contents!.Single(c => c is FunctionCallContent);
        fc.CallId.Should().Be("call_1");
        fc.Name.Should().Be("read_text_file");
        fc.Arguments.Should().NotBeNull();

        rendered[2].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.Tool);
        rendered[2].Contents.Should().ContainSingle(c => c is FunctionResultContent);
        var fr = (FunctionResultContent)rendered[2].Contents!.Single(c => c is FunctionResultContent);
        fr.CallId.Should().Be("call_1");
        fr.Result.Should().NotBeNull();

        rendered[3].Role.Should().Be(Microsoft.Extensions.AI.ChatRole.Assistant);
        rendered[3].Text.Should().Be("done");
    }
}
