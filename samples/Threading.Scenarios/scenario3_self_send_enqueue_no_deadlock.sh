#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen3-$(date +%s)"

echo "[scenario3] session=$SESSION"

NEW_OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario3] sessionId=$SESSION_ID"

# Turn 1: self-send enqueue (historical deadlock class)
acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly:
1) You may call at most 2 tools in this turn.
2) You may ONLY call: report_intent, thread_send.
3) You MUST NOT call any other tools (especially thread_start, thread_read, thread_list).
4) After the 2 tool calls complete, do NOT wait for any enqueued messages; output EXACTLY: AFTER_PING (nothing else).

Now do the work:
Call tool report_intent with arguments: {"intent":"self enqueue"}.
Then call tool thread_send with arguments: {"threadId":"main","delivery":"enqueue","message":"PING"}.
Then output EXACTLY: AFTER_PING'

echo "---"

# Turn 2: ensure tools still work in same session (catalog stability)
acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly:
1) You may call at most 2 tools in this turn.
2) You may ONLY call: report_intent, thread_list.
3) You MUST NOT call any other tools (especially thread_start, thread_send, thread_read).
4) After the 2 tool calls complete, output EXACTLY: DONE (nothing else).

Now do the work:
Call tool report_intent with arguments: {"intent":"list threads"}.
Then call tool thread_list with arguments: {}.
Then tell me the intent for the main thread.
Then output EXACTLY: DONE'
