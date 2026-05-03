using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Provider;
using Agent.Acp.Acp;

namespace Agent.Harness.Shell;

/// <summary>
/// PowerShell provider backing the <c>project:</c> drive.
///
/// File content operations map to ACP fs/read_text_file and fs/write_text_file.
/// Directory listing/navigation is resolved locally against the session cwd.
/// </summary>
[CmdletProvider("ProjectDrive", ProviderCapabilities.None)]
public sealed class ProjectDriveContentProvider : NavigationCmdletProvider, IContentCmdletProvider
{
    private const string ContextVarName = "__project_drive_ctx";

    protected override bool IsValidPath(string path) => true;

    private ProjectDrivePsContext GetCtx()
    {
        var value = SessionState.PSVariable.GetValue(ContextVarName);
        if (value is not ProjectDrivePsContext ctx)
            throw new InvalidOperationException("project_drive_context_missing");
        return ctx;
    }

    protected override bool ItemExists(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            // Keep content operations ACP-first even if the local tree does not know about the file yet.
            _ = GetCtx().ResolveProjectPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override bool IsItemContainer(string path)
    {
        try
        {
            var localPath = GetCtx().ResolveProjectPath(path);
            return Directory.Exists(localPath);
        }
        catch
        {
            return false;
        }
    }

    protected override bool HasChildItems(string path)
    {
        try
        {
            var localPath = GetCtx().ResolveProjectPath(path);
            return Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any();
        }
        catch
        {
            return false;
        }
    }

    protected override void GetItem(string path)
    {
        var localPath = GetCtx().ResolveProjectPath(path);
        if (Directory.Exists(localPath))
        {
            WriteItemObject(new DirectoryInfo(localPath), path, isContainer: true);
            return;
        }

        if (File.Exists(localPath))
        {
            WriteItemObject(new FileInfo(localPath), path, isContainer: false);
            return;
        }

        WriteError(new ErrorRecord(
            new ItemNotFoundException($"Cannot find path '{path}' because it does not exist."),
            "PathNotFound",
            ErrorCategory.ObjectNotFound,
            path));
    }

    protected override void GetChildItems(string path, bool recurse, uint depth)
    {
        var localPath = GetCtx().ResolveProjectPath(path);

        if (File.Exists(localPath))
        {
            WriteItemObject(new FileInfo(localPath), path, isContainer: false);
            return;
        }

        if (!Directory.Exists(localPath))
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"Cannot find path '{path}' because it does not exist."),
                "PathNotFound",
                ErrorCategory.ObjectNotFound,
                path));
            return;
        }

        WriteDirectoryChildren(new DirectoryInfo(localPath), recurse, depth);
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var localPath = GetCtx().ResolveProjectPath(path);
        if (!Directory.Exists(localPath))
            return;

        var dir = new DirectoryInfo(localPath);
        foreach (var child in dir.EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            WriteItemObject(child.Name, child.Name, isContainer: child is DirectoryInfo);
        }
    }

    public IContentReader GetContentReader(string path)
    {
        var ctx = GetCtx();
        var normalized = ctx.ResolveProjectPath(path);
        return new ProjectDriveContentReader(ctx, normalized);
    }

    public object? GetContentReaderDynamicParameters(string path) => null;

    public IContentWriter GetContentWriter(string path)
    {
        var ctx = GetCtx();
        var normalized = ctx.ResolveProjectPath(path);
        return new ProjectDriveContentWriter(ctx, normalized);
    }

    public object? GetContentWriterDynamicParameters(string path) => null;

    public void ClearContent(string path)
    {
        using var w = GetContentWriter(path);
        w.Close();
    }

    public object? ClearContentDynamicParameters(string path) => null;

    private void WriteDirectoryChildren(DirectoryInfo dir, bool recurse, uint depth)
    {
        foreach (var child in dir.EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            WriteItemObject(child, child.FullName, isContainer: child is DirectoryInfo);

            if (!recurse || child is not DirectoryInfo childDir)
                continue;

            if (depth == 0)
                continue;

            var nextDepth = depth == uint.MaxValue ? uint.MaxValue : depth - 1;
            WriteDirectoryChildren(childDir, recurse: true, nextDepth);
        }
    }

    private sealed class ProjectDriveContentReader : IContentReader
    {
        private readonly ProjectDrivePsContext _ctx;
        private readonly string _path;
        private bool _done;

        public ProjectDriveContentReader(ProjectDrivePsContext ctx, string path)
        {
            _ctx = ctx;
            _path = path;
        }

        public IList Read(long readCount)
        {
            if (_done) return Array.Empty<string>();
            _done = true;

            var client = _ctx.RequireClient();
            var resp = client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest
            {
                SessionId = _ctx.SessionId ?? string.Empty,
                Path = _path,
            }).GetAwaiter().GetResult();

            return new[] { resp.Content };
        }

        public void Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public void Close() { }
        public void Dispose() { }
    }

    private sealed class ProjectDriveContentWriter : IContentWriter
    {
        private readonly ProjectDrivePsContext _ctx;
        private readonly string _path;
        private readonly List<string> _chunks = new();

        public ProjectDriveContentWriter(ProjectDrivePsContext ctx, string path)
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
            var client = _ctx.RequireClient();
            var text = string.Join(Environment.NewLine, _chunks);

            client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest
            {
                SessionId = _ctx.SessionId ?? string.Empty,
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
