#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

# Long timeouts by default, because Ollama can be slow.
export ACP_TIMEOUT="${ACP_TIMEOUT:-1500}"
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds="${AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds:-1500}"

# For this demo we want shell suggestions ON, but keep report_intent suggestions OFF
# (report_intent suggestions are a separate feature, and disabling them keeps the run snappy).
export AGENTSERVER_AgentServer__Core__IncludeSuggestionsInReportIntent="${AGENTSERVER_AgentServer__Core__IncludeSuggestionsInReportIntent:-false}"
export AGENTSERVER_AgentServer__Core__IncludeSuggestionsInShell="${AGENTSERVER_AgentServer__Core__IncludeSuggestionsInShell:-true}"

# Use the deterministic heuristic suggester for this sample (no extra model call).
export AGENTSERVER_AgentServer__Core__UseHeuristicCommandSuggestions="${AGENTSERVER_AgentServer__Core__UseHeuristicCommandSuggestions:-true}"

# Ensure the server binary is up to date.
dotnet build Agent.slnx -c Release >/dev/null

SESSION="pwsh-intent-suggest-demo-$(date +%s)"

# Create a new session.
acpx --approve-all --non-interactive-permissions fail --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout "$ACP_TIMEOUT" sessions new --name "$SESSION" >/dev/null

echo "[pwsh-demo] session=$SESSION"

PROMPT_FILE="/tmp/acp-pwsh-intent-suggest-demo-prompt.txt"
cat > "$PROMPT_FILE" <<'EOF'
You are running an ACP demo. Follow the rules exactly.

Rules:
1) You MUST call tool report_intent first.
2) You MUST call ALL tools below EXACTLY ONCE and IN THIS ORDER:
   report_intent → agent_shell_execute
3) Between tool calls, output tool calls only (no natural language).
4) You MUST NOT output any XML/HTML tags like <tool_response> or <tool_result>.
5) If any tool fails, output EXACTLY: FAILED
6) After agent_shell_execute completes successfully, output EXACTLY: DONE

Now do the work (tool calls only):

Call tool report_intent with arguments: {"intent":"PowerShell shell intent-based command suggestions demo"}.

Call tool agent_shell_execute with arguments: {"script":"Find-AgentCommand -Intent 'list files under a directory' | Select-Object -First 8 | ConvertTo-Json -Compress"}.

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
