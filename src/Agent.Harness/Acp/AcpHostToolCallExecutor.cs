using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Tools.Executors;

namespace Agent.Harness.Acp;

public sealed class AcpHostToolCallExecutor : IToolCallExecutor
{
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly string? _sessionCwd;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;

    public AcpHostToolCallExecutor(
        string sessionId,
        IAcpClientCaller client,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null)
    {
        _sessionId = sessionId;
        _client = client;
        _sessionCwd = sessionCwd;
        _store = store;
    }

    public bool CanExecute(string toolName) => toolName is "read_text_file" or "write_text_file" or "patch_text_file" or "execute_command";

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        try
        {
            var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

            switch (tool.ToolName)
            {
                case "read_text_file":
                {
                    var path = NormalizeFsPath(GetRequiredString(args, "path"));

                    try
                    {
                        var resp = await _client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest
                        {
                            SessionId = _sessionId,
                            Path = path,
                        }, cancellationToken).ConfigureAwait(false);

                        var sha256 = Sha256Hex(resp.Content);

                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new
                        {
                            content = resp.Content,
                            sha256,
                        })));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"fs/read_text_file failed for path={path}: {ex.Message}", ex);
                    }
                }

                case "write_text_file":
                {
                    var path = NormalizeFsPath(GetRequiredString(args, "path"));
                    var content = GetRequiredString(args, "content");

                    try
                    {
                        await _client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest
                        {
                            SessionId = _sessionId,
                            Path = path,
                            Content = content,
                        }, cancellationToken).ConfigureAwait(false);

                        var sha256 = Sha256Hex(content);

                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new
                        {
                            ok = true,
                            sha256,
                        })));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"fs/write_text_file failed for path={path}: {ex.Message}", ex);
                    }
                }

                case "patch_text_file":
                {
                    var path = NormalizeFsPath(GetRequiredString(args, "path"));

                    var expectedSha = args.TryGetValue("expectedSha256", out var shaEl) && shaEl.ValueKind == JsonValueKind.String
                        ? shaEl.GetString()
                        : null;

                    if (!args.TryGetValue("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("missing_required:edits");

                    Agent.Acp.Schema.ReadTextFileResponse before;
                    try
                    {
                        before = await _client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest { SessionId = _sessionId, Path = path }, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"fs/read_text_file failed for path={path}: {ex.Message}", ex);
                    }

                    var content = before.Content;
                    var beforeSha = Sha256Hex(content);

                    if (!string.IsNullOrWhiteSpace(expectedSha))
                    {
                        if (!IsSha256Hex(expectedSha))
                            throw new InvalidOperationException($"invalid_args:expectedSha256_not_sha256:{expectedSha}");

                        if (!string.Equals(expectedSha, beforeSha, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"sha256_mismatch expected={expectedSha} actual={beforeSha}");
                    }

                    foreach (var edit in editsEl.EnumerateArray())
                        content = ApplyStructuredEdit(content, edit);

                    try
                    {
                        await _client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest { SessionId = _sessionId, Path = path, Content = content }, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"fs/write_text_file failed for path={path}: {ex.Message}", ex);
                    }

                    var afterSha = Sha256Hex(content);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new
                    {
                        ok = true,
                        beforeSha256 = beforeSha,
                        afterSha256 = afterSha,
                        appliedEdits = editsEl.GetArrayLength(),
                    })));
                }

                case "execute_command":
                {
                    var command = GetRequiredString(args, "command");

                    var argv = Array.Empty<string>();
                    if (args.TryGetValue("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                        argv = argsEl.Deserialize<string[]>() ?? Array.Empty<string>();

                    var created = await _client.CreateTerminalAsync(new Agent.Acp.Schema.CreateTerminalRequest
                    {
                        SessionId = _sessionId,
                        Command = command,
                        Args = argv.ToList(),
                    }, cancellationToken).ConfigureAwait(false);

                    await _client.WaitForTerminalExitAsync(new Agent.Acp.Schema.WaitForTerminalExitRequest { SessionId = _sessionId, TerminalId = created.TerminalId }, cancellationToken)
                        .ConfigureAwait(false);

                    var output = await _client.GetTerminalOutputAsync(new Agent.Acp.Schema.TerminalOutputRequest { SessionId = _sessionId, TerminalId = created.TerminalId }, cancellationToken)
                        .ConfigureAwait(false);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new
                    {
                        exitStatus = output.ExitStatus,
                        output = output.Output,
                        truncated = output.Truncated,
                    })));
                }

                default:
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(tool.ToolId, "unknown_tool"));
            }
        }
        catch (Exception ex)
        {
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(tool.ToolId, ex.Message));
        }
    }

    private string NormalizeFsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        var cwd = _sessionCwd;
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = _store?.TryLoadMetadata(_sessionId)?.Cwd;

        if (string.IsNullOrWhiteSpace(cwd))
            return Path.GetFullPath(rawPath);

        return Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(cwd, rawPath));
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }

    private static bool IsSha256Hex(string s)
    {
        if (s.Length != 64)
            return false;

        foreach (var ch in s)
        {
            var isHex =
                (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static string Sha256Hex(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ApplyStructuredEdit(string content, JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("invalid_args:edit_not_object");

        if (!edit.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("invalid_args:missing_required:op");

        var op = opEl.GetString();

        int? occurrence = null;
        if (edit.TryGetProperty("occurrence", out var occEl) && occEl.ValueKind == JsonValueKind.Number)
            occurrence = occEl.GetInt32();

        return op switch
        {
            "replace_exact" => ApplyReplaceExact(content,
                GetRequiredString(edit, "oldText"),
                GetRequiredString(edit, "newText"),
                occurrence),

            "delete_exact" => ApplyDeleteExact(content,
                GetRequiredString(edit, "text"),
                occurrence),

            "insert_before" => ApplyInsert(content,
                GetRequiredString(edit, "anchorText"),
                GetRequiredString(edit, "text"),
                after: false,
                occurrence),

            "insert_after" => ApplyInsert(content,
                GetRequiredString(edit, "anchorText"),
                GetRequiredString(edit, "text"),
                after: true,
                occurrence),

            _ => throw new InvalidOperationException($"invalid_args:unknown_op:{op}"),
        };
    }

    private static string GetRequiredString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"invalid_args:missing_required:{name}");
        return el.GetString() ?? string.Empty;
    }

    private static string ApplyDeleteExact(string content, string text, int? occurrence)
    {
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("invalid_args:text_empty");

        return ApplyReplaceExact(content, text, string.Empty, occurrence);
    }

    private static string ApplyReplaceExact(string content, string oldText, string newText, int? occurrence)
    {
        if (string.IsNullOrEmpty(oldText))
            throw new InvalidOperationException("invalid_args:oldText_empty");

        var idxs = AllIndexesOf(content, oldText);
        var idx = SelectIndex(idxs, oldText, occurrence);

        return content.Substring(0, idx) + newText + content.Substring(idx + oldText.Length);
    }

    private static string ApplyInsert(string content, string anchor, string text, bool after, int? occurrence)
    {
        if (string.IsNullOrEmpty(anchor))
            throw new InvalidOperationException("invalid_args:anchorText_empty");

        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("invalid_args:text_empty");

        var idxs = AllIndexesOf(content, anchor);
        var idx = SelectIndex(idxs, anchor, occurrence);
        var insertAt = after ? idx + anchor.Length : idx;

        return content.Substring(0, insertAt) + text + content.Substring(insertAt);
    }

    private static List<int> AllIndexesOf(string content, string needle)
    {
        var idxs = new List<int>();
        for (var i = 0;;)
        {
            var idx = content.IndexOf(needle, i, StringComparison.Ordinal);
            if (idx < 0) break;
            idxs.Add(idx);
            i = idx + Math.Max(needle.Length, 1);
        }

        return idxs;
    }

    private static int SelectIndex(List<int> idxs, string needle, int? occurrence)
    {
        if (occurrence is null)
        {
            if (idxs.Count == 0)
                throw new InvalidOperationException($"patch_failed:not_found:{needle}");
            if (idxs.Count != 1)
                throw new InvalidOperationException($"patch_failed:multiple_matches:{needle}:{idxs.Count}");

            return idxs[0];
        }

        if (occurrence == 0)
            throw new InvalidOperationException("invalid_args:occurrence_must_be_positive");

        if (idxs.Count == 0)
            throw new InvalidOperationException($"patch_failed:not_found:{needle}");

        var idx = occurrence > 0
            ? occurrence.Value - 1
            : idxs.Count + occurrence.Value;

        if (idx < 0 || idx >= idxs.Count)
            throw new InvalidOperationException($"patch_failed:occurrence_out_of_range:{needle}:{occurrence}:{idxs.Count}");

        return idxs[idx];
    }
}
