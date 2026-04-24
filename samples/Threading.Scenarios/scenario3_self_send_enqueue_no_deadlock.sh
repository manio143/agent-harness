#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen3-$(date +%s)"

echo "[scenario3] session=$SESSION"

NEW_OUT="$(acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
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
# NOTE: A single `acpx prompt` request may invoke the model multiple times as the harness drains work.
# This prompt intentionally handles that multi-invocation behavior.
acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
  'You MUST follow these rules exactly.

This single request may invoke you MULTIPLE TIMES. Each time you are invoked, inspect the conversation history and follow the FIRST matching rule.

Allowed tools: report_intent, thread_send.
Tool rules:
- You may call at most 2 tools per invocation.
- If you call any tools, you MUST call report_intent first, then thread_send.
- Do NOT simulate tool calls as text/JSON — you MUST actually call the tools.

Rules (apply in order):
1) If the history contains <inter_thread ...>PING</inter_thread>:
   - You MUST NOT call any tools.
   - Output EXACTLY: PING
   - Then output EXACTLY: AFTER_PING
   - Stop.

2) Else if the history already contains a tool result for thread_send (i.e. you have already called thread_send in this request):
   - You MUST NOT call any tools.
   - Output EXACTLY: WAITING
   - Stop.

3) Else (first invocation, PING not yet delivered):
   - Call tool report_intent with arguments: {"intent":"self enqueue"}.
   - Then call tool thread_send with arguments: {"threadId":"main","delivery":"enqueue","message":"PING"}.
   - Then output EXACTLY: WAITING
   - Stop.'

echo "---"

# Turn 2: ensure tools still work in same session (catalog stability)
acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
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
