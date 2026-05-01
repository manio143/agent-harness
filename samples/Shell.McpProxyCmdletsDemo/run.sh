#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

export ACP_TIMEOUT="${ACP_TIMEOUT:-1500}"
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds="${AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds:-1500}"

# Build first.
dotnet build Agent.slnx -c Release >/dev/null

SESSION="pwsh-mcp-proxy-demo-$(date +%s)"

# Start a new session with the sample cwd so .acpxrc.json is picked up.
cd samples/Shell.McpProxyCmdletsDemo
npx -y acpx@latest --cwd . --approve-all --non-interactive-permissions fail --timeout "$ACP_TIMEOUT" sessions new --name "$SESSION" >/dev/null

echo "[pwsh-mcp-proxy-demo] session=$SESSION"

PROMPT_FILE="/tmp/acp-pwsh-mcp-proxy-demo-prompt.txt"
cat > "$PROMPT_FILE" <<'EOF'
You are running an ACP demo. Follow the rules exactly.

Rules:
1) You MUST call tool report_intent first.
2) You MUST call ALL tools below EXACTLY ONCE and IN THIS ORDER:
   report_intent → agent_shell_execute → agent_shell_execute
3) Between tool calls, output tool calls only (no natural language).
4) You MUST NOT output any XML/HTML tags like <tool_response> or <tool_result>.
5) If any tool fails, output EXACTLY: FAILED
6) After the final agent_shell_execute completes successfully, output EXACTLY: DONE

Now do the work (tool calls only):

Call tool report_intent with arguments: {"intent":"PowerShell MCP proxy cmdlets demo"}.

Call tool agent_shell_execute with arguments: {"script":"(Get-Module -Name 'Mcp.everything') -ne $null"}.

Call tool agent_shell_execute with arguments: {"script":"$r = Get-Sum -A 40 -B 2; $r | ConvertTo-Json -Compress"}.

Then output EXACTLY: DONE.
EOF

OUT="$(npx -y acpx@latest --cwd . --approve-all --non-interactive-permissions fail --timeout "$ACP_TIMEOUT" prompt -s "$SESSION" -f "$PROMPT_FILE")"

if echo "$OUT" | grep -qx "DONE"; then
  echo "DONE"
  exit 0
fi

echo "$OUT" >&2
if echo "$OUT" | rg -q "FAILED"; then
  exit 1
fi

echo "Expected final line to be DONE" >&2
exit 1
