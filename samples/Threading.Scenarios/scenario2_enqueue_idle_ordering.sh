#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen2-$(date +%s)"

echo "[scenario2] session=$SESSION"

NEW_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario2] sessionId=$SESSION_ID"

recover_child_thread_id_from_committed() {
  local sess_id="$1"
  local events_file=".agent/sessions/$sess_id/threads/main/events.jsonl"

  if [[ ! -f "$events_file" ]]; then
    return 0
  fi

  python3 - "$events_file" <<'PY'
import json, pathlib, sys
p = pathlib.Path(sys.argv[1])
lines = p.read_text(encoding='utf-8').splitlines()
last_req = None
for line in lines:
    try:
        o = json.loads(line)
    except Exception:
        continue
    if o.get('type') == 'tool_call_requested' and o.get('toolName') == 'thread_start':
        last_req = o.get('toolId')
thread_id = None
if last_req:
    for line in lines:
        try:
            o = json.loads(line)
        except Exception:
            continue
        if o.get('type') == 'tool_call_completed' and o.get('toolId') == last_req:
            thread_id = (o.get('result') or {}).get('threadId')
            break
print(thread_id or '')
PY
}

# Turn 1: create child.
# NOTE: We intentionally parse the created threadId from the tool output instead of assuming a fixed name.
set +e
TURN1_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
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
   - Then call tool thread_start with arguments: {"name":"child_ready","context":"new","mode":"single","delivery":"immediate","message":"Say READY. Do NOT call any tools.","capabilities":{"deny":["*"]}}.
   - Then output EXACTLY: OK
   - Stop.

3) Else:
   - Call tool report_intent with arguments: {"intent":"create child"}.
   - Then call tool thread_start with arguments: {"name":"child_ready","context":"new","mode":"single","delivery":"immediate","message":"Say READY. Do NOT call any tools.","capabilities":{"deny":["*"]}}.
   - Then output EXACTLY: OK
   - Stop.')"
TURN1_CODE=$?
set -e

echo "$TURN1_OUT"
if [[ "$TURN1_CODE" != "0" && "$TURN1_CODE" != "124" ]]; then
  echo "[scenario2] Turn 1 failed with exit code $TURN1_CODE" >&2
  exit "$TURN1_CODE"
fi

THREADS_DIR=".agent/sessions/$SESSION_ID/threads"
CHILD_ID="$(echo "$TURN1_OUT" | rg -o '"threadId":\s*"[^"]+"' | tail -n 1 | sed 's/"threadId":\s*"//; s/"$//' || true)"

if [[ -z "$CHILD_ID" ]]; then
  if [[ "$TURN1_CODE" == "124" ]]; then
    echo "[scenario2] Turn 1 timed out (124); attempting recovery from committed logs..." >&2
  else
    echo "[scenario2] Turn 1 produced no threadId in stdout; attempting recovery from committed logs..." >&2
  fi
  for _ in $(seq 1 10); do
    CHILD_ID="$(recover_child_thread_id_from_committed "$SESSION_ID" | tr -d '[:space:]' || true)"
    if [[ -n "$CHILD_ID" ]]; then
      break
    fi
    sleep 0.2
  done
fi

if [[ -z "$CHILD_ID" ]]; then
  echo "Failed to determine child threadId for Turn 1." >&2
  exit "${TURN1_CODE:-1}"
fi

echo "[scenario2] childThreadId=$CHILD_ID"

echo "---"

# Turn 2: enqueue follow-up to the child.
timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  "You MUST follow these rules exactly.

This single request may invoke you MULTIPLE TIMES. Each time you are invoked, inspect the conversation history and follow the FIRST matching rule.

Allowed tools: report_intent, thread_send.
Tool rules:
- You may call at most 2 tools per invocation.
- If you call any tools, you MUST call report_intent first, then thread_send.
- When calling thread_send you MUST include: \"threadId\":\"$CHILD_ID\". Copy the arguments EXACTLY.

Rules (apply in order):
1) If the history contains a successful tool result for thread_send:
   - You MUST NOT call any tools.
   - Output EXACTLY: DONE
   - Stop.

2) Else if the history shows thread_send failed with \"missing_required:threadId\":
   - Call tool report_intent with arguments: {\"intent\":\"retry thread_send with threadId\"}.
   - Then call tool thread_send with arguments: {\"threadId\":\"$CHILD_ID\",\"delivery\":\"enqueue\",\"message\":\"Now say CONSUMED. Do NOT call any tools.\"}.
   - Then output EXACTLY: DONE
   - Stop.

3) Else:
   - Call tool report_intent with arguments: {\"intent\":\"enqueue followup\"}.
   - Then call tool thread_send with arguments: {\"threadId\":\"$CHILD_ID\",\"delivery\":\"enqueue\",\"message\":\"Now say CONSUMED. Do NOT call any tools.\"}.
   - Then output EXACTLY: DONE
   - Stop."

echo "---"

# Turn 3: wait for idle notification and summarize.
timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly:
1) You may call at most 1 tool in this turn.
2) You may ONLY call: report_intent.
3) You MUST NOT call any other tools.
4) After you receive the child idle notification in main, output EXACTLY: DONE (nothing else).

Now do the work:
Call tool report_intent with arguments: {"intent":"await idle"}.
Wait until you receive the child idle notification in main. Then summarize what happened in 2 bullet points.
Then output EXACTLY: DONE'

# Tip for manual inspection (not asserted here):
# - committed logs live under src/Agent.Server/.agent/sessions (see appsettings.json)
