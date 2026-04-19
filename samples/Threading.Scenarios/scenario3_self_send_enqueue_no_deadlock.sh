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
# Goal: enqueue a self-message and ensure it is eventually delivered (promoted) without deadlocking.
acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly:
1) You may call at most 2 tools in this turn.
2) You may ONLY call: report_intent, thread_send.
3) You MUST NOT call any other tools.
4) You MUST call report_intent exactly once with arguments: {"intent":"self enqueue"}.
5) You MUST then call thread_send exactly once with arguments: {"threadId":"main","delivery":"enqueue","message":"PING"}.
6) After the 2 tool calls complete, output EXACTLY: OK (nothing else).

Now do the work:
Call tool report_intent with arguments: {"intent":"self enqueue"}.
Then call tool thread_send with arguments: {"threadId":"main","delivery":"enqueue","message":"PING"}.
Then output EXACTLY: OK'

# Deterministic assertions from event log (avoid relying on model to “notice” PING).
EVENTS_FILE=".agent/sessions/$SESSION_ID/threads/main/events.jsonl"

if [[ ! -f "$EVENTS_FILE" ]]; then
  echo "Missing events file: $EVENTS_FILE" >&2
  exit 1
fi

# Wait up to 30s for enqueue promotion to show up.
# Use fixed-string matching to avoid regex escaping issues.
if ! timeout 30s bash -lc "until rg -F -q '\"type\":\"inter_thread_message\"' '$EVENTS_FILE' && rg -F -q '\"text\":\"PING\"' '$EVENTS_FILE'; do sleep 0.5; done"; then
  echo "Expected inter_thread_message PING not found in: $EVENTS_FILE" >&2
  exit 1
fi

THREAD_SEND_COUNT="$(rg -F -c '"toolName":"thread_send"' "$EVENTS_FILE" || true)"
if [[ "$THREAD_SEND_COUNT" != "1" ]]; then
  echo "WARNING: expected exactly 1 thread_send tool call, got $THREAD_SEND_COUNT" >&2
  rg -F -n '"toolName":"thread_send"' "$EVENTS_FILE" | tail -n 20 >&2 || true
fi

echo "[scenario3] OK: PING delivered. thread_send count=$THREAD_SEND_COUNT"