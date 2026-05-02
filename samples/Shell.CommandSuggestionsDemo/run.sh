#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

# Long timeouts by default, because Ollama can be slow.
export ACP_TIMEOUT="${ACP_TIMEOUT:-1500}"

# Main model call timeout (seconds). Default: 5 minutes.
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds="${AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds:-300}"

# For this demo we want shell suggestions ON, but keep report_intent suggestions OFF
# (report_intent suggestions are a separate feature, and disabling them keeps the run snappy).
export AGENTSERVER_AgentServer__Core__IncludeSuggestionsInReportIntent="${AGENTSERVER_AgentServer__Core__IncludeSuggestionsInReportIntent:-false}"
export AGENTSERVER_AgentServer__Core__IncludeSuggestionsInShell="${AGENTSERVER_AgentServer__Core__IncludeSuggestionsInShell:-true}"

# IMPORTANT: Find-AgentCommand uses the "quick-work" model. Force it to a known-good local model
# for this sample, to avoid hanging if the configured quick-work model isn't available.
export AGENTSERVER_AgentServer__Models__QuickWorkModel="${AGENTSERVER_AgentServer__Models__QuickWorkModel:-qwen}"

# Ensure the server binary is up to date.
dotnet build Agent.slnx -c Release >/dev/null

SESSION="pwsh-intent-suggest-demo-$(date +%s)"

# Log LLM prompts so we can inspect the exact suggestion prompt.
: "${AGENTSERVER_AgentServer__Logging__LogLlmPrompts:=true}"
export AGENTSERVER_AgentServer__Logging__LogLlmPrompts

# Log ACP JSON-RPC traffic (stderr) to help debug any acpx hangs.
: "${AGENTSERVER_AgentServer__Logging__LogRpc:=true}"
export AGENTSERVER_AgentServer__Logging__LogRpc


# Create a new session.
NEW_OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout "$ACP_TIMEOUT" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

# Retry prompt once if acpx reports a transient reconnect.
attempt=0

echo "[pwsh-demo] session=$SESSION"
echo "[pwsh-demo] sessionId=$SESSION_ID"

PROMPT_FILE="/tmp/acp-pwsh-intent-suggest-demo-prompt.txt"
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

Call tool report_intent with arguments: {"intent":"PowerShell shell intent-based command suggestions demo"}.

Call tool agent_shell_execute with arguments: {"script":"$global:s = Find-AgentCommand -Intent 'list files under a directory'; $global:s | ConvertTo-Json -Compress"}.

Call tool agent_shell_execute with arguments: {"script":"if (-not $global:s -or $global:s.Count -eq 0) { throw 'Find-AgentCommand returned no suggestions' }; $cmd = $global:s[0].name; & $cmd -Path sandbox:\\ | Select-Object -First 5 Name | ConvertTo-Json -Compress"}.

Then output EXACTLY: DONE.
EOF

while true; do
  attempt=$((attempt+1))

  set +e
  OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" \
    --timeout "$ACP_TIMEOUT" \
    prompt -s "$SESSION" -f "$PROMPT_FILE" 2>&1)"
  STATUS=$?
  set -e

  if [[ $STATUS -ne 0 ]] && ! echo "$OUT" | rg -q "agent needs reconnect"; then
    echo "$OUT" >&2
    exit $STATUS
  fi

  # Some runs fail fast with a transient reconnect message.
  if echo "$OUT" | rg -q "agent needs reconnect" && [[ $attempt -lt 2 ]]; then
    echo "[pwsh-demo] reconnect detected; retrying once..." >&2
    continue
  fi

  # The prompt demands the model outputs a DONE line on success.
  # Note: acpx may include additional status lines in the same output.
  if echo "$OUT" | grep -q "^DONE$"; then
    echo "DONE"
    exit 0
  fi

  echo "$OUT" >&2
  if echo "$OUT" | rg -q "FAILED"; then
    exit 1
  fi

  echo "Expected a DONE line in output" >&2
  exit 1
done
