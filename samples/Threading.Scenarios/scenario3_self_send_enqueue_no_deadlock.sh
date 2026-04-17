#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen3-$(date +%s)"

echo "[scenario3] session=$SESSION"

NEW_OUT="$(acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario3] sessionId=$SESSION_ID"

# Restrict tool catalog to keep the sample deterministic (permission boundary via tool declarations).
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" set -s "$SESSION" tool_allowlist threading_no_fork >/dev/null

# Turn 1: self-send enqueue (historical deadlock class)
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'Call tool report_intent with arguments: {"intent":"self enqueue"}.
Then call tool thread_send with arguments: {"threadId":"main","delivery":"enqueue","message":"PING"}.
After the tool completes, print exactly: AFTER_PING'

echo "---"

# Turn 2: ensure tools still work in same session (catalog stability)
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'Call tool report_intent with arguments: {"intent":"list threads"}.
Then call tool thread_list with arguments: {}.
Then tell me the intent for the main thread.
Finally print exactly: DONE'
