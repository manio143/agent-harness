using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Llm;

/// <summary>
/// Creates MEAI function/tool content objects across MEAI version differences.
///
/// We use reflection here because MEAI has had minor ctor shape differences across releases.
/// The harness needs to be tolerant so we can keep the functional core stable.
/// </summary>
public static class MeaiFunctionContentFactory
{
    public static FunctionCallContent CreateFunctionCall(string callId, string name, JsonElement args)
    {
        // Best-effort conversion of args to a dictionary (most MEAI builds expect that).
        var dict = args.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(args.GetRawText()) ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();

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

            // Older/alternate shapes (best-effort): (string name, string argsJson)
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
            {
                return (FunctionCallContent)c.Invoke(new object[] { name, args.GetRawText() });
            }
        }

        throw new InvalidOperationException("No compatible FunctionCallContent ctor found");
    }

    public static FunctionResultContent CreateFunctionResult(string callId, object result)
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
}
