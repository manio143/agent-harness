#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen3-$(date +%s)"

echo "[scenario3] session=$SESSION"

acpx --agent "$AGENT_CMD" --timeout 180 sessions new --name "$SESSION" >/dev/null

# Turn 1: self-send enqueue (historical deadlock class)
acpx --agent "$AGENT_CMD" --timeout 180 prompt -s "$SESSION" \
  'Call thread_send targeting threadId="main" with delivery="enqueue" and message="PING".
Then continue and print exactly: AFTER_PING'

echo "---"

# Turn 2: ensure tools still work in same session (catalog stability)
acpx --agent "$AGENT_CMD" --timeout 180 prompt -s "$SESSION" \
  'Now call thread_list and tell me the intent for the main thread. Then print exactly: DONE'
