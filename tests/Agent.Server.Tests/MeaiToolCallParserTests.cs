using System.Text.Json;
using Agent.Harness;
using Agent.Server;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Server.Tests;

public sealed class MeaiToolCallParserTests
{
    private static ChatResponseUpdate CreateUpdateWithContents(params AIContent[] contents)
    {
        var update = Activator.CreateInstance<ChatResponseUpdate>();
        var prop = typeof(ChatResponseUpdate).GetProperty("Contents");
        if (prop is null)
            throw new InvalidOperationException("ChatResponseUpdate.Contents not found");

        // Most MEAI builds expose Contents as IList<AIContent> or IReadOnlyList<AIContent>.
        if (prop.CanWrite)
        {
            prop.SetValue(update, contents.ToList());
        }
        else
        {
            // If init-only, try to set backing field (best-effort)
            var field = typeof(ChatResponseUpdate).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(f => f.Name.Contains("Contents", StringComparison.OrdinalIgnoreCase));
            field?.SetValue(update, contents.ToList());
        }

        return update;
    }

    private static FunctionCallContent CreateFunctionCallContent(string name, JsonElement arguments)
    {
        var ctors = typeof(FunctionCallContent).GetConstructors();

        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            // Common shapes: (string name, JsonElement args) OR (string name, string args)
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(JsonElement))
                return (FunctionCallContent)c.Invoke(new object[] { name, arguments });

            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                return (FunctionCallContent)c.Invoke(new object[] { name, arguments.GetRawText() });
        }

        throw new InvalidOperationException("No compatible FunctionCallContent ctor found");
    }

    private static FunctionResultContent CreateFunctionResultContent(string name, AIContent result)
    {
        var ctors = typeof(FunctionResultContent).GetConstructors();
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && typeof(AIContent).IsAssignableFrom(ps[1].ParameterType))
                return (FunctionResultContent)c.Invoke(new object[] { name, result });

            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                return (FunctionResultContent)c.Invoke(new object[] { name, (result as TextContent)?.Text ?? "" });
        }

        throw new InvalidOperationException("No compatible FunctionResultContent ctor found");
    }
    [Fact]
    public void ParseUpdate_WhenFunctionCallContent_EmitsObservedToolCallDetected_WithJsonArgs()
    {
        // WHY THIS IS AN INVARIANT:
        // Mode A requires the model to propose tool-call intent. The server must surface that
        // intent as an ObservedToolCallDetected event (not auto-invoke any callback).

        var content = CreateFunctionCallContent(
            name: "read_text_file",
            arguments: JsonSerializer.SerializeToElement(new { path = "/tmp/a.txt" }));

        var update = CreateUpdateWithContents(content);

        var observed = MeaiToolCallParser.Parse(update).OfType<ObservedToolCallDetected>().Single();

        observed.ToolName.Should().Be("read_text_file");
        observed.Args.Should().NotBeNull();
    }

    [Fact]
    public void ParseUpdate_WhenFunctionResultContent_DoesNotEmitToolCallDetected()
    {
        // FunctionResultContent is not a model proposal; it is an execution output.
        // In Mode A we treat execution output as ObservedToolCall* events from executors, not from the LLM.

        var content = CreateFunctionResultContent(
            name: "read_text_file",
            result: new TextContent("ok"));

        var update = CreateUpdateWithContents(content);

        MeaiToolCallParser.Parse(update).OfType<ObservedToolCallDetected>().Should().BeEmpty();
    }
}
