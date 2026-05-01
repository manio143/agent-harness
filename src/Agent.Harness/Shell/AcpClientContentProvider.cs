using System.Collections;
using System.Management.Automation.Provider;
using Agent.Acp.Acp;

namespace Agent.Harness.Shell;

/// <summary>
/// PowerShell provider backing the <c>client:</c> drive.
///
/// Maps content operations to ACP fs/read_text_file and fs/write_text_file.
/// Rooted at the ACP session cwd; only relative paths are allowed.
///
/// Listing is intentionally not supported (ACP has no list APIs).
/// </summary>
[CmdletProvider("AcpClient", ProviderCapabilities.None)]
public sealed class AcpClientContentProvider : NavigationCmdletProvider, IContentCmdletProvider
{
    protected override bool IsValidPath(string path) => true;

    private const string ContextVarName = "__acp_client_ctx";

    private AcpClientPsContext GetCtx()
    {
        var value = SessionState.PSVariable.GetValue(ContextVarName);
        if (value is not AcpClientPsContext ctx)
            throw new InvalidOperationException("acp_client_context_missing");
        return ctx;
    }

    private string NormalizeProviderPathToClientRelative(string path)
    {
        // Provider paths for drives usually arrive without the drive prefix, e.g. "foo\\bar.txt".
        // Treat leading separators as drive-root relative.
        var p = (path ?? string.Empty).TrimStart('\\', '/');
        return p;
    }

    protected override bool ItemExists(string path)
    {
        // Without ACP listing/stat APIs, we can't know.
        // Returning true avoids some cmdlets pre-checking existence and failing early.
        return true;
    }

    public IContentReader GetContentReader(string path)
    {
        var ctx = GetCtx();
        var rel = NormalizeProviderPathToClientRelative(path);
        var normalized = ctx.NormalizeClientRelativePath(rel);
        return new AcpClientContentReader(ctx, normalized);
    }

    public object? GetContentReaderDynamicParameters(string path) => null;

    public IContentWriter GetContentWriter(string path)
    {
        var ctx = GetCtx();
        var rel = NormalizeProviderPathToClientRelative(path);
        var normalized = ctx.NormalizeClientRelativePath(rel);
        return new AcpClientContentWriter(ctx, normalized);
    }

    public object? GetContentWriterDynamicParameters(string path) => null;

    public void ClearContent(string path)
    {
        using var w = GetContentWriter(path);
        w.Close();
    }

    public object? ClearContentDynamicParameters(string path) => null;

    private sealed class AcpClientContentReader : IContentReader
    {
        private readonly AcpClientPsContext _ctx;
        private readonly string _path;
        private bool _done;

        public AcpClientContentReader(AcpClientPsContext ctx, string path)
        {
            _ctx = ctx;
            _path = path;
        }

        public IList Read(long readCount)
        {
            if (_done) return Array.Empty<string>();
            _done = true;

            // ACP returns whole file.
            var resp = _ctx.Client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest
            {
                SessionId = _ctx.SessionId,
                Path = _path,
            }).GetAwaiter().GetResult();

            return new[] { resp.Content };
        }

        public void Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public void Close() { }

        public void Dispose() { }
    }

    private sealed class AcpClientContentWriter : IContentWriter
    {
        private readonly AcpClientPsContext _ctx;
        private readonly string _path;
        private readonly List<string> _chunks = new();

        public AcpClientContentWriter(AcpClientPsContext ctx, string path)
        {
            _ctx = ctx;
            _path = path;
        }

        public IList Write(IList content)
        {
            foreach (var item in content)
            {
                if (item is null) continue;
                _chunks.Add(item.ToString() ?? string.Empty);
            }

            return Array.Empty<string>();
        }

        public void Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public void Close()
        {
            var text = string.Join(Environment.NewLine, _chunks);

            _ctx.Client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest
            {
                SessionId = _ctx.SessionId,
                Path = _path,
                Content = text,
            }).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            // PowerShell calls Close() explicitly.
        }
    }
}
