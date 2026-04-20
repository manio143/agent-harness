#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen2-$(date +%s)"

echo "[scenario2] session=$SESSION"

NEW_OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario2] sessionId=$SESSION_ID"

# Turn 1: create child.
# NOTE: We intentionally parse the created threadId from the tool output instead of assuming a fixed name.
TURN1_OUT="$(acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly:
1) You will call EXACTLY 2 tools in this turn.
2) You may ONLY call: report_intent, thread_start.
3) You MUST NOT call any other tools.
4) Do NOT explain. Do NOT add any extra text.
5) After the 2 tool calls complete, output EXACTLY: OK

Now do the work:
Call tool report_intent with arguments: {"intent":"create child"}.
Then call tool thread_start with arguments: {"name":"child_ready","context":"new","delivery":"immediate","message":"Say READY. Do NOT call any tools."}.
Then output EXACTLY: OK')"

echo "$TURN1_OUT"

THREADS_DIR=".agent/sessions/$SESSION_ID/threads"
CHILD_ID="$(echo "$TURN1_OUT" | rg -o '"threadId":\s*"[^"]+"' | tail -n 1 | sed 's/"threadId":\s*"//; s/"$//')"

if [[ -z "$CHILD_ID" ]]; then
  echo "Failed to parse child threadId from Turn 1 output" >&2
  exit 1
fi

echo "[scenario2] childThreadId=$CHILD_ID"

echo "---"

# Turn 2: enqueue follow-up to the child.
acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  "You MUST follow these rules exactly:
1) You may call at most 2 tools in this turn.
2) You may ONLY call: report_intent, thread_send.
3) You MUST NOT call any other tools (especially thread_start, thread_read, thread_list).
4) After the 2 tool calls complete, output EXACTLY: DONE (nothing else).

Now do the work:
Call tool report_intent with arguments: {\"intent\":\"enqueue followup\"}.
Then call tool thread_send with arguments: {\"threadId\":\"$CHILD_ID\",\"delivery\":\"enqueue\",\"message\":\"Now say CONSUMED. Do NOT call any tools.\"}.
Then output EXACTLY: DONE."

echo "---"

# Turn 3: wait for idle notification and summarize.
acpx --approve-all --non-interactive-permissions fail --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
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
