#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

# Long timeouts by default, because Ollama can be slow.
export ACP_TIMEOUT="${ACP_TIMEOUT:-1500}"
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds="${AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds:-1500}"

# Ensure the server binary is up to date.
dotnet build Agent.slnx -c Release >/dev/null

SESSION="pwsh-client-drive-demo-$(date +%s)"

# Create a new session.
acpx --approve-all --non-interactive-permissions fail --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout "$ACP_TIMEOUT" sessions new --name "$SESSION" >/dev/null

echo "[pwsh-demo] session=$SESSION"

PROMPT_FILE="/tmp/acp-pwsh-client-drive-demo-prompt.txt"
cat > "$PROMPT_FILE" <<'EOF'
You are running an ACP demo. Follow the rules exactly.

Rules:
1) You MUST call tool report_intent first.
2) You MUST call ALL tools below EXACTLY ONCE and IN THIS ORDER:
   report_intent → agent_shell_execute → agent_shell_execute → agent_shell_execute
3) Between tool calls, output tool calls only (no natural language).
4) You MUST NOT output any XML/HTML tags like <tool_response> or <tool_result>.
5) If any tool fails, output EXACTLY: FAILED
6) After the final agent_shell_execute completes successfully, output EXACTLY: DONE

Now do the work (tool calls only):

Call tool report_intent with arguments: {"intent":"PowerShell shell + client drive demo"}.

Call tool agent_shell_execute with arguments: {"script":"$x = 41; $x + 1"}.

Call tool agent_shell_execute with arguments: {"script":"'hello-sandbox' | Set-Content -Path sandbox:\\local.txt; Get-Content -Path sandbox:\\local.txt"}.

Call tool agent_shell_execute with arguments: {"script":"'hello-client' | Set-Content -Path client:\\remote.txt; Get-Content -Path client:\\remote.txt"}.

Then output EXACTLY: DONE.
EOF

OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" \
  --timeout "$ACP_TIMEOUT" \
  prompt -s "$SESSION" -f "$PROMPT_FILE")"

# The prompt demands the model outputs exactly DONE on success.
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
