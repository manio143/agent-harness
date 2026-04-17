#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen2-$(date +%s)"

echo "[scenario2] session=$SESSION"

NEW_OUT="$(acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario2] sessionId=$SESSION_ID"

# Restrict tool catalog to keep the sample deterministic (permission boundary via tool declarations).
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" set -s "$SESSION" tool_allowlist threading_no_fork >/dev/null

# Turn 1: create child.
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'Call tool report_intent with arguments: {"intent":"create child"}.
Then call tool thread_new with arguments: {"delivery":"immediate","message":"Say READY"}.
Then reply with exactly: OK'

THREADS_DIR=".agent/sessions/$SESSION_ID/threads"

CHILD_ID=""
for _ in $(seq 1 40); do
  if [[ -d "$THREADS_DIR" ]]; then
    CHILD_ID="$(ls -1 "$THREADS_DIR" 2>/dev/null | grep -v '^main$' | head -n 1 || true)"
    if [[ -n "$CHILD_ID" ]]; then
      break
    fi
  fi
  sleep 0.25
done

if [[ -z "$CHILD_ID" ]]; then
  echo "Failed to find child thread under $THREADS_DIR" >&2
  exit 1
fi

echo "[scenario2] childThreadId=$CHILD_ID"

echo "---"

# Turn 2: enqueue follow-up to the child.
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  "Call tool report_intent with arguments: {\"intent\":\"enqueue followup\"}.
Then call tool thread_send with arguments: {\"threadId\":\"$CHILD_ID\",\"delivery\":\"enqueue\",\"message\":\"Now say CONSUMED\"}.
Then say ENQUEUED_OK.
Then reply with exactly: DONE."

echo "---"

# Turn 3: wait for idle notification and summarize.
acpx --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'Call tool report_intent with arguments: {"intent":"await idle"}.
Wait until you receive the child idle notification in main. Then summarize what happened in 2 bullet points.
Then reply with exactly: DONE'

# Tip for manual inspection (not asserted here):
# - committed logs live under src/Agent.Server/.agent/sessions (see appsettings.json)
