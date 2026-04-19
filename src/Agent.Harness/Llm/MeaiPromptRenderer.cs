using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Agent.Harness.Threads;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Llm;

/// <summary>
/// Renders committed harness events into MEAI messages.
///
/// Tool call + tool result history must be expressed using MEAI tool/function content
/// (FunctionCallContent + FunctionResultContent).
/// </summary>
public static class MeaiPromptRenderer
{
    private static Microsoft.Extensions.AI.ChatMessage CreateWithContents(Microsoft.Extensions.AI.ChatRole role, params AIContent[] contents)
    {
        // MEAI ChatMessage ctor shapes vary slightly; we avoid depending on a specific one.
        // Create a message with empty text and then set Contents via reflection if needed.
        var msg = new Microsoft.Extensions.AI.ChatMessage(role, "");

        var prop = typeof(Microsoft.Extensions.AI.ChatMessage).GetProperty("Contents");
        if (prop is null)
            return msg;

        if (prop.CanWrite)
        {
            prop.SetValue(msg, contents.ToList());
            return msg;
        }

        // init-only: try backing field (best-effort)
        var field = typeof(Microsoft.Extensions.AI.ChatMessage).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(f => f.Name.Contains("Contents", StringComparison.OrdinalIgnoreCase));
        field?.SetValue(msg, contents.ToList());

        return msg;
    }

    public static List<Microsoft.Extensions.AI.ChatMessage> Render(SessionState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();

        foreach (var evt in state.Committed)
        {
            switch (evt)
            {
                case UserMessage u:
                    messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, u.Text));
                    break;

                case AssistantMessage a:
                    messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, a.Text));
                    break;

                case InterThreadMessage it:
                    messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"<inter_thread from=\"{it.FromThreadId}\">{it.Text}</inter_thread>"));
                    break;

                case ThreadIdleNotification n:
                    messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"<thread_idle child=\"{n.ChildThreadId}\" intent=\"{n.LastIntent}\" />"));
                    break;

                case NewThreadTask t:
                {
                    messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, Agent.Harness.Threads.NewThreadTaskMarkup.Render(t)));
                    break;
                }

                case ToolCallRequested t:
                {
                    var fc = MeaiFunctionContentFactory.CreateFunctionCall(t.ToolId, t.ToolName, t.Args);
                    messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Assistant, fc));
                    break;
                }

                case ToolCallUpdate u:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = u.ToolId, content = u.Content }, json);
                    messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"<tool_update>{payload}</tool_update>"));
                    break;
                }

                case ToolCallCompleted c:
                {
                    var fr = MeaiFunctionContentFactory.CreateFunctionResult(c.ToolId, c.Result);
                    messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                    break;
                }

                case ToolCallFailed f:
                {
                    var fr = MeaiFunctionContentFactory.CreateFunctionResult(f.ToolId, new { outcome = "failed", error = f.Error });
                    messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                    break;
                }

                case ToolCallRejected r:
                {
                    var fr = MeaiFunctionContentFactory.CreateFunctionResult(r.ToolId, new { outcome = "rejected", reason = r.Reason, details = r.Details });
                    messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                    break;
                }

                case ToolCallCancelled c:
                {
                    var fr = MeaiFunctionContentFactory.CreateFunctionResult(c.ToolId, new { outcome = "cancelled" });
                    messages.Add(CreateWithContents(Microsoft.Extensions.AI.ChatRole.Tool, fr));
                    break;
                }

                default:
                    break;
            }
        }

        return messages;
    }
}
