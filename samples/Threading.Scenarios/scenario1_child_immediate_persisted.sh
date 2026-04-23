#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen1-$(date +%s)"

echo "[scenario1] session=$SESSION"

NEW_OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
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
# NOTE: Parse the created threadId from the tool output instead of assuming a fixed name.
set +e
TURN1_OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly.

This single request may invoke you MULTIPLE TIMES. Each time you are invoked, inspect the conversation history and follow the FIRST matching rule.

Allowed tools: report_intent, thread_start.
Tool rules:
- You may call at most 2 tools per invocation.
- If you call any tools, you MUST call report_intent first, then thread_start.
- Do NOT call thread_start more than once in this request.

Rules (apply in order):
1) If the history already contains a tool result for thread_start:
   - You MUST NOT call any tools.
   - Output EXACTLY: OK
   - Stop.

2) Else (first invocation):
   - Call tool report_intent with arguments: {"intent":"create child"}.
   - Then call tool thread_start with arguments: {"name":"perm_boundary","context":"new","mode":"single","delivery":"immediate","message":"In 1 paragraph, explain how the tool catalog acts as the permission boundary in this harness. Do NOT call any tools. Do NOT ask questions."}.
   - Then output EXACTLY: OK
   - Stop.')"
TURN1_CODE=$?
set -e

echo "$TURN1_OUT"
if [[ "$TURN1_CODE" != "0" ]]; then
  echo "[scenario1] Turn 1 failed with exit code $TURN1_CODE" >&2
  exit "$TURN1_CODE"
fi

THREADS_DIR=".agent/sessions/$SESSION_ID/threads"
CHILD_ID="$(echo "$TURN1_OUT" | rg -o '"threadId":\s*"[^"]+"' | tail -n 1 | sed 's/"threadId":\s*"//; s/"$//')"

if [[ -z "$CHILD_ID" ]]; then
  echo "Failed to parse child threadId from Turn 1 output" >&2
  exit 1
fi

echo "[scenario1] childThreadId=$CHILD_ID"

# Wait for the child to actually produce an assistant message (Ollama latency can be significant).
CHILD_EVENTS="$THREADS_DIR/$CHILD_ID/events.jsonl"
for _ in $(seq 1 120); do
  if [[ -f "$CHILD_EVENTS" ]] && grep -q '"type":"assistant_message"' "$CHILD_EVENTS"; then
    break
  fi
  sleep 0.5
done

echo "---"

# Turn 2: read child explicitly.
acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  "You MUST follow these rules exactly:
1) You may call at most 2 tools in this turn.
2) You may ONLY call: report_intent, thread_read.
3) You MUST NOT call any other tools (especially thread_start, thread_send, thread_list).
4) After the 2 tool calls complete, output EXACTLY: DONE (nothing else).

Now do the work:
Call tool report_intent with arguments: {\"intent\":\"read child\"}.
The child threadId is: $CHILD_ID.
Then call tool thread_read with arguments: {\"threadId\":\"${CHILD_ID}\"}.
Then paste the child assistant message text verbatim.
Then output EXACTLY: DONE"
