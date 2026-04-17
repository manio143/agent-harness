#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

# Long timeouts by default, because Ollama can be slow.
export ACP_TIMEOUT="${ACP_TIMEOUT:-1500}"
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds="${AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds:-1500}"

# Ensure the server binary is up to date.
dotnet build Agent.slnx -c Release >/dev/null

SESSION="patch-demo-$(date +%s)"

# Create a new session.
acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout "$ACP_TIMEOUT" sessions new --name "$SESSION" >/dev/null

echo "[patch-demo] session=$SESSION"

# Prompt:
# - enforce tool order
# - use sha256 from read_text_file as expectedSha256 in patch_text_file
PROMPT_FILE="/tmp/acp-patch-demo-prompt.txt"
cat > "$PROMPT_FILE" <<'EOF'
You are running an ACP demo. Follow the rules exactly.

Rules:
1) You MUST call tool report_intent first.
2) You MUST call ALL tools below EXACTLY ONCE and IN THIS ORDER:
   report_intent → write_text_file → read_text_file → patch_text_file → read_text_file
3) Between tool calls, output tool calls only (no natural language).
4) When calling patch_text_file, you MUST include path, expectedSha256, and edits. No missing required fields.
5) The expectedSha256 you pass MUST exactly match the sha256 from the prior read_text_file tool result. For this file content ("hello world"), the sha256 is exactly: b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9
6) If any tool fails, output EXACTLY: FAILED
7) After the final read_text_file completes successfully, output EXACTLY: DONE

Now do the work (tool calls only):
Call tool report_intent with arguments: {"intent":"patch_text_file demo"}.
Call tool write_text_file with arguments: {"path":"demo.txt","content":"hello world"}.
Call tool read_text_file with arguments: {"path":"demo.txt"}.
Call tool patch_text_file with arguments: {"path":"demo.txt","expectedSha256":"b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9","edits":[{"op":"replace_exact","oldText":"world","newText":"there"}]}.
Call tool read_text_file with arguments: {"path":"demo.txt"}.
Then output EXACTLY: DONE.
EOF

OUT="$(acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" \
  --timeout "$ACP_TIMEOUT" \
  prompt -s "$SESSION" -f "$PROMPT_FILE")"

# The prompt demands the model outputs exactly DONE on success.
if echo "$OUT" | tail -n 1 | grep -qx "DONE"; then
  echo "DONE"
  exit 0
fi

echo "$OUT" >&2
if echo "$OUT" | rg -q "FAILED"; then
  exit 1
fi

echo "Expected final line to be DONE" >&2
exit 1
