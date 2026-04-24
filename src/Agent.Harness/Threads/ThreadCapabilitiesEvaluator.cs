using System.Collections.Immutable;

namespace Agent.Harness.Threads;

/// <summary>
/// Computes effective per-thread tool availability based on ThreadMetadata.Capabilities (allow/deny selectors).
///
/// IMPORTANT: This is NOT a security sandbox. If a thread can execute commands, it can usually write files.
/// Capabilities are a tool-surface restriction.
/// </summary>
public static class ThreadCapabilitiesEvaluator
{
    public static ImmutableHashSet<string> ExpandSelectors(
        ImmutableArray<ToolDefinition> tools,
        ImmutableArray<string> selectors)
    {
        if (selectors.IsDefaultOrEmpty)
            return ImmutableHashSet<string>.Empty;

        var toolNames = tools.Select(t => t.Name).ToImmutableHashSet(StringComparer.Ordinal);
        var result = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var raw in selectors)
        {
            var sel = (raw ?? string.Empty).Trim();
            if (sel.Length == 0)
                continue;

            if (sel == "*")
            {
                result.UnionWith(toolNames);
                continue;
            }

            switch (sel)
            {
                case "threads":
                    foreach (var n in toolNames)
                        if (n.StartsWith("thread_", StringComparison.Ordinal))
                            result.Add(n);
                    break;

                case "fs.read":
                    if (toolNames.Contains("read_text_file")) result.Add("read_text_file");
                    break;

                case "fs.write":
                    if (toolNames.Contains("write_text_file")) result.Add("write_text_file");
                    if (toolNames.Contains("patch_text_file")) result.Add("patch_text_file");
                    break;

                case "host.exec":
                    if (toolNames.Contains("execute_command")) result.Add("execute_command");
                    break;

                case "mcp:*":
                    foreach (var n in toolNames)
                        if (n.Contains("__", StringComparison.Ordinal))
                            result.Add(n);
                    break;

                default:
                    if (sel.StartsWith("mcp:", StringComparison.Ordinal))
                    {
                        var rest = sel[4..];
                        if (rest == "*")
                        {
                            foreach (var n in toolNames)
                                if (n.Contains("__", StringComparison.Ordinal))
                                    result.Add(n);
                        }
                        else
                        {
                            var colon = rest.IndexOf(':');
                            if (colon >= 0)
                            {
                                var server = rest[..colon];
                                var tool = rest[(colon + 1)..];

                                if (server.Length > 0 && tool.Length > 0)
                                {
                                    if (tool == "*")
                                    {
                                        foreach (var n in toolNames)
                                            if (n.StartsWith(server + "__", StringComparison.Ordinal))
                                                result.Add(n);
                                    }
                                    else
                                    {
                                        var exact = server + "__" + tool;
                                        if (toolNames.Contains(exact))
                                            result.Add(exact);
                                    }
                                }
                            }
                            else
                            {
                                var server = rest;
                                if (server.Length > 0)
                                {
                                    foreach (var n in toolNames)
                                        if (n.StartsWith(server + "__", StringComparison.Ordinal))
                                            result.Add(n);
                                }
                            }
                        }
                    }
                    break;
            }
        }

        return result.ToImmutable();
    }

    public static ImmutableArray<ToolDefinition> FilterToolsForThread(
        string sessionId,
        string threadId,
        ImmutableArray<ToolDefinition> tools,
        IThreadStore store)
    {
        // Always allow report_intent (tool policy dependency).
        var allowed = ComputeEffectiveAllowedToolNames(sessionId, threadId, tools, store);
        var filtered = tools.Where(t => allowed.Contains(t.Name)).ToImmutableArray();

        // Ensure report_intent remains available if present.
        if (tools.Any(t => t.Name == ToolSchemas.ReportIntent.Name) && !filtered.Any(t => t.Name == ToolSchemas.ReportIntent.Name))
            filtered = filtered.Insert(0, ToolSchemas.ReportIntent);

        return filtered;
    }

    public static bool IsToolAllowed(
        string sessionId,
        string threadId,
        string toolName,
        ImmutableArray<ToolDefinition> tools,
        IThreadStore store)
    {
        if (string.Equals(toolName, ToolSchemas.ReportIntent.Name, StringComparison.Ordinal))
            return true;

        var allowed = ComputeEffectiveAllowedToolNames(sessionId, threadId, tools, store);
        return allowed.Contains(toolName);
    }

    private static ImmutableHashSet<string> ComputeEffectiveAllowedToolNames(
        string sessionId,
        string threadId,
        ImmutableArray<ToolDefinition> tools,
        IThreadStore store)
    {
        var universe = tools.Select(t => t.Name).ToImmutableHashSet(StringComparer.Ordinal);
        universe = universe.Add(ToolSchemas.ReportIntent.Name);

        ImmutableHashSet<string> BaseForRoot() => universe;

        ImmutableHashSet<string> Apply(
            ImmutableHashSet<string> inherited,
            ThreadCapabilitiesSpec? spec)
        {
            if (spec is null)
                return inherited;

            var allowExpanded = ExpandSelectors(tools, spec.Allow);
            var denyExpanded = ExpandSelectors(tools, spec.Deny);

            // If allow list is non-empty, start from empty; else start from inherited.
            var start = spec.Allow.IsDefaultOrEmpty
                ? inherited
                : ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

            var result = start;
            if (!allowExpanded.IsEmpty)
                result = result.Union(allowExpanded);

            if (!denyExpanded.IsEmpty)
                result = result.Except(denyExpanded);

            // Monotonic: never exceed inherited.
            result = result.Intersect(inherited);

            // report_intent always allowed.
            result = result.Add(ToolSchemas.ReportIntent.Name);

            return result;
        }

        // Walk parent chain (main defaults to full universe).
        ThreadMetadata? meta = store.TryLoadThreadMetadata(sessionId, threadId);
        if (meta is null)
            return universe.Add(ToolSchemas.ReportIntent.Name);

        ImmutableHashSet<string> effective;
        if (string.Equals(meta.ThreadId, ThreadIds.Main, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(meta.ParentThreadId))
            effective = Apply(BaseForRoot(), meta.Capabilities);
        else
        {
            effective = ComputeEffectiveAllowedToolNames(sessionId, meta.ParentThreadId!, tools, store);
            effective = Apply(effective, meta.Capabilities);
        }

        return effective;
    }
}
