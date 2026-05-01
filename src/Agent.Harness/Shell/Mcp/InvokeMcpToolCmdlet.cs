using System.Collections;
using System.Management.Automation;

namespace Agent.Harness.Shell.Mcp;

[Cmdlet(VerbsLifecycle.Invoke, "McpTool")]
public sealed class InvokeMcpToolCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true)]
    public string Server { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public string Name { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public Hashtable Args { get; set; } = null!;

    [Parameter]
    public SwitchParameter RawOutput { get; set; }

    protected override void ProcessRecord()
    {
        var ctxObj = SessionState.PSVariable.GetValue("__mcpCtx");
        if (ctxObj is not McpToolPsContext ctx)
            throw new InvalidOperationException("mcp_context_missing");

        var result = ctx.Invoke(Server, Name, Args, RawOutput.IsPresent);
        WriteObject(result);
    }
}
