#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen1-$(date +%s)"

echo "[scenario1] session=$SESSION"

NEW_OUT="$(acpx --agent "$AGENT_CMD" --timeout 180 sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  # Some acpx builds print just the UUID.
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario1] sessionId=$SESSION_ID"

# Turn 1: create child.
acpx --agent "$AGENT_CMD" --timeout 240 prompt -s "$SESSION" \
  'Call thread_new with delivery="immediate" and message: "Explain how the tool catalog acts as the permission boundary in this harness".'

# Grab the child thread id from the persisted thread store.
THREADS_DIR=".agent/sessions/$SESSION_ID/threads"

# Wait (briefly) for the orchestrator to persist the new child thread directory.
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
  ls -la ".agent/sessions/$SESSION_ID" >&2 || true
  exit 1
fi

echo "[scenario1] childThreadId=$CHILD_ID"

echo "---"

# Turn 2: read child explicitly.
acpx --agent "$AGENT_CMD" --timeout 240 prompt -s "$SESSION" \
  "Call thread_read with threadId=\"$CHILD_ID\" and then paste the child assistant message text verbatim."
