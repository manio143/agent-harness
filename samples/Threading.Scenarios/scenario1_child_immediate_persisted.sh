#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen1-$(date +%s)"

echo "[scenario1] session=$SESSION"

acpx --agent "$AGENT_CMD" --timeout 180 sessions new --name "$SESSION" >/dev/null

acpx --agent "$AGENT_CMD" --timeout 180 prompt -s "$SESSION" \
  'Create a child thread to answer: "Explain how the tool catalog acts as the permission boundary in this harness". Use delivery=immediate. Then tell me the child thread id.'

echo "---"

# Second prompt: read child thread.
acpx --agent "$AGENT_CMD" --timeout 180 prompt -s "$SESSION" \
  'Now thread_list to find the child thread id you created, then thread_read it and quote the child assistant message verbatim.'
