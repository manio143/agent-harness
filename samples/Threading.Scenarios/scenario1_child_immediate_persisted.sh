#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen1-$(date +%s)"

echo "[scenario1] session=$SESSION"

NEW_OUT="$(acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
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
TURN1_OUT="$(acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly.

This single request may invoke you MULTIPLE TIMES. Each time you are invoked, inspect the conversation history and follow the FIRST matching rule.

Allowed tools: report_intent, thread_start.
Tool rules:
- You may call at most 2 tools per invocation.
- If you call any tools, you MUST call report_intent first, then thread_start.
- When calling thread_start you MUST include: "mode":"single". Copy the arguments EXACTLY.

Rules (apply in order):
1) If the history contains a successful tool result for thread_start (it includes a "threadId"):
   - You MUST NOT call any tools.
   - Output EXACTLY: OK
   - Stop.

2) Else if the history shows thread_start failed with "missing_required:mode":
   - Call tool report_intent with arguments: {"intent":"retry thread_start with mode"}.
   - Then call tool thread_start with arguments: {"name":"perm_boundary","context":"new","mode":"single","delivery":"immediate","message":"In 1 paragraph, explain how the tool catalog acts as the permission boundary in this harness. Do NOT call any tools. Do NOT ask questions.","capabilities":{"deny":["*"]}}.
   - Then output EXACTLY: OK
   - Stop.

3) Else (first invocation):
   - Call tool report_intent with arguments: {"intent":"create child"}.
   - Then call tool thread_start with arguments: {"name":"perm_boundary","context":"new","mode":"single","delivery":"immediate","message":"In 1 paragraph, explain how the tool catalog acts as the permission boundary in this harness. Do NOT call any tools. Do NOT ask questions.","capabilities":{"deny":["*"]}}.
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
CHILD_ID="$(echo "$TURN1_OUT" | rg -o '"threadId":\s*"[^"]+"' | tail -n 1 | sed 's/"threadId":\s*"//; s/"$//' || true)"

if [[ -z "$CHILD_ID" ]]; then
  echo "Failed to parse child threadId from Turn 1 output (model likely did not call thread_start)." >&2
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
acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  "You MUST follow these rules exactly.

This single request may invoke you MULTIPLE TIMES. Each time you are invoked, inspect the conversation history and follow the FIRST matching rule.

Allowed tools: report_intent, thread_read.
Tool rules:
- You may call at most 2 tools per invocation.
- If you call any tools, you MUST call report_intent first, then thread_read.
- When calling thread_read you MUST include: \"threadId\":\"$CHILD_ID\". Copy the arguments EXACTLY.

Rules (apply in order):
1) If the history contains a successful tool result for thread_read (it includes a \"messages\" array):
   - You MUST NOT call any tools.
   - Paste the child assistant message text verbatim.
   - Then output EXACTLY: DONE
   - Stop.

2) Else if the history shows thread_read failed with \"missing_required:threadId\":
   - Call tool report_intent with arguments: {\"intent\":\"retry thread_read with threadId\"}.
   - Then call tool thread_read with arguments: {\"threadId\":\"$CHILD_ID\"}.
   - Then paste the child assistant message text verbatim.
   - Then output EXACTLY: DONE
   - Stop.

3) Else:
   - Call tool report_intent with arguments: {\"intent\":\"read child\"}.
   - Then call tool thread_read with arguments: {\"threadId\":\"$CHILD_ID\"}.
   - Then paste the child assistant message text verbatim.
   - Then output EXACTLY: DONE
   - Stop."
