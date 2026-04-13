using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Llm;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Acp;

public sealed class AcpEffectExecutor : IEffectExecutor
{
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;

    public AcpEffectExecutor(IAcpClientCaller client, MeaiIChatClient chat)
    {
        _client = client;
        _chat = chat;
    }

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case CallModel:
                return await CallModelAsync(state, cancellationToken).ConfigureAwait(false);

            case CheckPermission p:
                return CheckPermission(state, p);

            case ExecuteToolCall t:
                return await ExecuteToolAsync(t, cancellationToken).ConfigureAwait(false);

            default:
                return ImmutableArray<ObservedChatEvent>.Empty;
        }
    }

    private ImmutableArray<ObservedChatEvent> CheckPermission(SessionState state, CheckPermission p)
    {
        // MVP: deterministic capability-only gating.
        var known = state.Tools.Any(t => t.Name == p.ToolName);
        if (!known)
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionDenied(p.ToolId, "unknown_tool"));

        return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "capability_present"));
    }

    private async Task<ImmutableArray<ObservedChatEvent>> CallModelAsync(SessionState state, CancellationToken cancellationToken)
    {
        var rendered = Core.RenderPrompt(state);
        var meaiMessages = rendered
            .Select(m => new MeaiChatMessage(m.Role switch
            {
                ChatRole.User => MeaiChatRole.User,
                ChatRole.Assistant => MeaiChatRole.Assistant,
                _ => MeaiChatRole.System,
            }, m.Text))
            .ToList();

        var updates = _chat.GetStreamingResponseAsync(meaiMessages, cancellationToken: cancellationToken);

        var observed = new List<ObservedChatEvent>();
        await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken).ConfigureAwait(false))
            observed.Add(o);

        return observed.ToImmutableArray();
    }

    private async Task<ImmutableArray<ObservedChatEvent>> ExecuteToolAsync(ExecuteToolCall t, CancellationToken cancellationToken)
    {
        // Args can be JsonElement (committed ToolCallRequested.Args) or a plain dictionary (from detection).
        var args = NormalizeArgs(t.Args);

        switch (t.ToolName)
        {
            case "read_text_file":
            {
                var path = GetRequiredString(args, "path");
                var resp = await _client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest { Path = path }, cancellationToken)
                    .ConfigureAwait(false);
                return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, new { content = resp.Content }));
            }

            case "write_text_file":
            {
                var path = GetRequiredString(args, "path");
                var content = GetRequiredString(args, "content");
                await _client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest { Path = path, Content = content }, cancellationToken)
                    .ConfigureAwait(false);
                return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, new { ok = true }));
            }

            case "execute_command":
            {
                var command = GetRequiredString(args, "command");

                var created = await _client.CreateTerminalAsync(new Agent.Acp.Schema.CreateTerminalRequest { Command = command }, cancellationToken)
                    .ConfigureAwait(false);

                // MVP: wait for exit then pull output.
                await _client.WaitForTerminalExitAsync(new Agent.Acp.Schema.WaitForTerminalExitRequest { TerminalId = created.TerminalId }, cancellationToken)
                    .ConfigureAwait(false);

                var output = await _client.GetTerminalOutputAsync(new Agent.Acp.Schema.TerminalOutputRequest { TerminalId = created.TerminalId }, cancellationToken)
                    .ConfigureAwait(false);

                return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, new
                {
                    exitStatus = output.ExitStatus,
                    output = output.Output,
                    truncated = output.Truncated,
                }));
            }

            default:
                return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(t.ToolId, "unknown_tool"));
        }
    }

    private static Dictionary<string, JsonElement> NormalizeArgs(object args)
    {
        if (args is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return je.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        }

        // best-effort: serialize then parse
        var parsed = JsonSerializer.SerializeToElement(args);
        if (parsed.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>();

        return parsed.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }
}
