using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Agent.Harness;
using Agent.Harness.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests.Llm;

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
        var callId = "call_1";
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(arguments.GetRawText())
            ?? new Dictionary<string, object>();

        foreach (var c in typeof(FunctionCallContent).GetConstructors())
        {
            var ps = c.GetParameters();

            // MEAI 10.4.1: (string callId, string name, IDictionary<string, object> arguments)
            if (ps.Length == 3
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(string)
                && ps[2].ParameterType.IsAssignableFrom(dict.GetType()))
            {
                return (FunctionCallContent)c.Invoke(new object[] { callId, name, dict });
            }

            // Older/alternate shapes (best-effort)
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                return (FunctionCallContent)c.Invoke(new object[] { name, arguments.GetRawText() });
        }

        throw new InvalidOperationException("No compatible FunctionCallContent ctor found");
    }

    private static FunctionResultContent CreateFunctionResultContent(string callId, object result)
    {
        foreach (var c in typeof(FunctionResultContent).GetConstructors())
        {
            var ps = c.GetParameters();

            // MEAI 10.4.1: (string callId, object result)
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(object))
                return (FunctionResultContent)c.Invoke(new object[] { callId, result });
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
            callId: "call_1",
            result: new TextContent("ok"));

        var update = CreateUpdateWithContents(content);

        MeaiToolCallParser.Parse(update).OfType<ObservedToolCallDetected>().Should().BeEmpty();
    }
}
