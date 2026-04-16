#!/usr/bin/env bash
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="scen2-$(date +%s)"

echo "[scenario2] session=$SESSION"

acpx --agent "$AGENT_CMD" --timeout 240 sessions new --name "$SESSION" >/dev/null

# Single prompt: create child immediate, then enqueue followup to child.
acpx --agent "$AGENT_CMD" --timeout 240 prompt -s "$SESSION" \
  'Create a child thread with message "Say READY" using delivery=immediate.
Then enqueue a follow-up message to that SAME child: "Now say CONSUMED" using delivery=enqueue.
Finally, wait until you receive the child idle notification in main and then summarize what happened in 2 bullet points.'

# Tip for manual inspection (not asserted here):
# - committed logs live under src/Agent.Server/.agent/sessions (see appsettings.json)
