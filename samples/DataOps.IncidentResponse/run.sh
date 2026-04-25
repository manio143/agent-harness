#!/usr/bin/env bash
# DataOps Incident Response Scenario
# Multi-phase, multi-turn evaluation with thread coordination and tool failure recovery
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="dataops-$(date +%s)"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$SCRIPT_DIR"

# User-secrets (including Groq config) are loaded only when DOTNET_ENVIRONMENT=Development.
# For sample runs we default to Development unless the caller overrides it.
if [[ -z "${DOTNET_ENVIRONMENT:-}" ]]; then
  export DOTNET_ENVIRONMENT=Development
fi
if [[ -z "${ASPNETCORE_ENVIRONMENT:-}" ]]; then
  export ASPNETCORE_ENVIRONMENT="$DOTNET_ENVIRONMENT"
fi

# For constrained providers (e.g., Groq free-tier), keep tool-result payloads small.
# This caps *observed/committed* tool outputs and (for non-read tools) writes raw results to a thread file.
: "${AGENTSERVER_AgentServer__ToolResultCapping__Enabled:=true}"
: "${AGENTSERVER_AgentServer__ToolResultCapping__MaxStringChars:=128}"
: "${AGENTSERVER_AgentServer__ToolResultCapping__MaxArrayItems:=10}"
: "${AGENTSERVER_AgentServer__ToolResultCapping__MaxObjectProperties:=20}"
export AGENTSERVER_AgentServer__ToolResultCapping__Enabled AGENTSERVER_AgentServer__ToolResultCapping__MaxStringChars AGENTSERVER_AgentServer__ToolResultCapping__MaxArrayItems AGENTSERVER_AgentServer__ToolResultCapping__MaxObjectProperties

# We run the agent with --cwd "$SAMPLE_DIR" so relative paths like data/*.csv work.
# That would break a relative Agent.Server.dll path passed from the repo root, so normalize it.
REPO_ROOT="$(cd "$SAMPLE_DIR/../.." && pwd)"
read -r -a _agent_words <<<"$AGENT_CMD"
if [[ "${_agent_words[0]:-}" == "dotnet" && "${_agent_words[1]:-}" != "" && "${_agent_words[1]}" != /* ]]; then
  _agent_words[1]="$REPO_ROOT/${_agent_words[1]}"
  AGENT_CMD="${_agent_words[*]}"
fi
unset _agent_words

echo "[scenario] DataOps Incident Response - Session: $SESSION"

# Helper: Create new session
NEW_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" sessions new --name "$SESSION")"
SESSION_ID="$(echo "$NEW_OUT" | sed -n 's/.*(\([0-9a-f-]\{36\}\)).*/\1/p' | tail -n 1)"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(echo "$NEW_OUT" | tr -d '[:space:]')"
fi

if [[ ! "$SESSION_ID" =~ ^[0-9a-f-]{36}$ ]]; then
  echo "Failed to parse session id from: $NEW_OUT" >&2
  exit 1
fi

echo "[scenario] sessionId=$SESSION_ID"

# Helper: Extract thread ID from events
extract_thread_id() {
  local sess_id="$1"
  local events_file="$SAMPLE_DIR/.agent/sessions/$sess_id/threads/main/events.jsonl"
  
  if [[ ! -f "$events_file" ]]; then
    echo ""
    return
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

# ============================================================
# PHASE 1: Data Validation with Intentional Failure Recovery
# ============================================================
echo ""
echo "========== PHASE 1: Data Validation =========="

set +e
PHASE1_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"You are a DataOps incident response agent. Follow these rules EXACTLY.

CONTEXT:
- Working directory: $SAMPLE_DIR
- Incident: INC-2026-04-24-001 (temperature spike on sensors)
- Your task: Phase 1 Data Validation

ALLOWED TOOLS: report_intent, read_text_file, write_text_file, execute_command, everything__echo

TOOL BUDGET: Maximum 15 tool calls for this phase.

PHASE 1 REQUIREMENTS (execute in order):
1. Report intent: {\"intent\": \"Phase 1: Data Validation\"}
2. Read incident metadata: data/incident.json
3. Read sensor data: data/sensors.csv
4. ATTEMPT to read malformed data: data/sensors_malformed.csv
   - This WILL fail with parsing errors
   - When it fails, you MUST:
     a) Report intent: {\"intent\": \"recovered from malformed data\"}
     b) Log the error to out/errors.log using write_text_file
     c) Continue with valid data only
5. Parse and validate the sensor CSV data
6. Identify sensors with critical status (temperature >30°C)
7. Count status distribution (ok/warning/alert/critical)
8. Create validation output: out/phase1_validation.json with this structure:
   {
     \"incident_id\": \"INC-2026-04-24-001\",
     \"validation_timestamp\": \"<current_time>\",
     \"data_sources\": {
       \"sensors\": {\"path\": \"data/sensors.csv\", \"records\": <count>, \"valid\": true},
       \"sensors_malformed\": {\"path\": \"data/sensors_malformed.csv\", \"valid\": false, \"error\": \"<error_msg>\"}
     },
     \"anomalies\": {
       \"critical_sensors\": [\"sensor-001\", \"sensor-003\"],
       \"warning_sensors\": [<list>]
     },
     \"status_summary\": {
       \"ok\": <count>,
       \"warning\": <count>,
       \"alert\": <count>,
       \"critical\": <count>
     }
   }
9. Use MCP tool everything__echo with {\"message\": \"Phase 1 validation complete\"} as checkpoint
10. Output EXACTLY: PHASE_1_COMPLETE

FAILURE HANDLING:
- If ANY tool fails (except sensors_malformed.csv which is expected), stop and output: PHASE_1_FAILED
- If you exceed tool budget, output: PHASE_1_BUDGET_EXCEEDED

NO natural language between tool calls. Only tool calls and the final status message.")"
PHASE1_CODE=$?
set -e

echo "$PHASE1_OUT"

if [[ "$PHASE1_CODE" != "0" && "$PHASE1_CODE" != "124" ]]; then
  echo "[scenario] Phase 1 failed with exit code $PHASE1_CODE" >&2
  exit "$PHASE1_CODE"
fi

if ! echo "$PHASE1_OUT" | grep -q "PHASE_1_COMPLETE"; then
  echo "[scenario] Phase 1 missing completion sentinel; nudging once..." >&2

  set +e
  PHASE1_OUT_2="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"Continue Phase 1 now.

Do NOT stop after the report_intent tool call. Keep using tools until:
- out/phase1_findings.json exists
- out/errors.log exists (capture any parsing errors)

Then output EXACTLY: PHASE_1_COMPLETE")"
  PHASE1_CODE_2=$?
  set -e

  echo "$PHASE1_OUT_2"
  PHASE1_OUT="$PHASE1_OUT"$'\n'"$PHASE1_OUT_2"

  if [[ "$PHASE1_CODE_2" != "0" && "$PHASE1_CODE_2" != "124" ]]; then
    echo "[scenario] Phase 1 retry failed with exit code $PHASE1_CODE_2" >&2
    exit "$PHASE1_CODE_2"
  fi

  if ! echo "$PHASE1_OUT" | grep -q "PHASE_1_COMPLETE"; then
    echo "[scenario] Phase 1 missing completion sentinel (continuing to validation)" >&2
  fi
fi

echo "[scenario] Validating Phase 1 output..."
PHASE1_VALIDATE_RETRIES="${SCENARIO_VALIDATE_RETRIES:-2}"
PHASE1_VALIDATE_ATTEMPT=0
while true; do
  set +e
  PHASE1_VALIDATE_OUT="$(bash "$SAMPLE_DIR/scripts/validate_phase1.sh" 2>&1)"
  PHASE1_VALIDATE_CODE=$?
  set -e

  if [[ "$PHASE1_VALIDATE_CODE" == "0" ]]; then
    echo "$PHASE1_VALIDATE_OUT"
    break
  fi

  echo "$PHASE1_VALIDATE_OUT" >&2

  if (( PHASE1_VALIDATE_ATTEMPT >= PHASE1_VALIDATE_RETRIES )); then
    echo "[scenario] Phase 1 validation failed after retries" >&2
    exit 1
  fi

  PHASE1_VALIDATE_ATTEMPT=$((PHASE1_VALIDATE_ATTEMPT + 1))
  PHASE1_VALIDATE_SNIP="$(printf "%s" "$PHASE1_VALIDATE_OUT" | head -c 1200)"

  echo "[scenario] Phase 1 validation failed; asking agent to fix outputs (attempt $PHASE1_VALIDATE_ATTEMPT/$PHASE1_VALIDATE_RETRIES)..." >&2

  set +e
  FIX1_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"Phase 1 outputs failed validation. Fix ONLY the files in out/ so that scripts/validate_phase1.sh passes.

Rules:
- First call report_intent
- Keep tool usage minimal

Validator output (truncated):
$PHASE1_VALIDATE_SNIP

When done, output EXACTLY: PHASE_1_COMPLETE")"
  FIX1_CODE=$?
  set -e

  echo "$FIX1_OUT"

  if [[ "$FIX1_CODE" != "0" && "$FIX1_CODE" != "124" ]]; then
    echo "[scenario] Phase 1 fix attempt failed with exit code $FIX1_CODE" >&2
    exit "$FIX1_CODE"
  fi
done

# ============================================================
# PHASE 2: Multi-Thread Analysis (Isolation & Statistics)
# ============================================================
echo ""
echo "========== PHASE 2: Multi-Thread Analysis =========="

set +e
PHASE2_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"You are continuing incident response. Follow these rules EXACTLY.

CONTEXT:
- Working directory: $SAMPLE_DIR
- Phase 1 completed successfully
- Now executing Phase 2: Multi-Thread Analysis

ALLOWED TOOLS: report_intent, thread_start, thread_send, thread_read, thread_list, read_text_file, write_text_file, execute_command

TOOL BUDGET: Maximum 20 tool calls for this phase.

PHASE 2 REQUIREMENTS (execute in order):
1. Report intent: {\"intent\": \"Phase 2: Multi-Thread Analysis\"}
2. Read validation results: out/phase1_validation.json
3. Create TWO child threads (mode: \"single\") for parallel analysis:
   Thread A: {\"name\": \"analyze-sensor-001\", \"mode\": \"single\", \"delivery\": \"immediate\", \"message\": \"Analyze sensor-001 from data/sensors.csv. Calculate: min/max/avg temperature, trend (increasing/decreasing), duration above 25°C. Output JSON to out/sensor-001-stats.json. Use only read_text_file and write_text_file. No other tools.\", \"capabilities\": {\"deny\": [\"*\"], \"allow\": [\"read_text_file\", \"write_text_file\"]}}
   Thread B: {\"name\": \"analyze-sensor-003\", \"mode\": \"single\", \"delivery\": \"immediate\", \"message\": \"Analyze sensor-003 from data/sensors.csv. Calculate: min/max/avg temperature, trend (increasing/decreasing), duration above 25°C. Output JSON to out/sensor-003-stats.json. Use only read_text_file and write_text_file. No other tools.\", \"capabilities\": {\"deny\": [\"*\"], \"allow\": [\"read_text_file\", \"write_text_file\"]}}
4. List all threads using thread_list to verify both children exist
5. Read results from BOTH child threads using thread_read
6. Read sensor configuration: data/sensor_config.yaml
7. Aggregate findings and create analysis report: out/phase2_analysis.md
   Include:
   - Sensor Analysis section with statistics from both threads
   - Cross-reference with sensor_config.yaml thresholds
   - Recommendations (immediate actions, investigation steps)
   - Trend analysis (temperature increasing on both affected sensors)
8. Output EXACTLY: PHASE_2_COMPLETE

FAILURE HANDLING:
- If any thread_start fails, retry ONCE with corrected parameters
- If child threads don't produce output within expected time, report and continue
- If you exceed tool budget, output: PHASE_2_BUDGET_EXCEEDED

NO natural language between tool calls. Only tool calls and the final status message.")"
PHASE2_CODE=$?
set -e

echo "$PHASE2_OUT"

if [[ "$PHASE2_CODE" != "0" && "$PHASE2_CODE" != "124" ]]; then
  echo "[scenario] Phase 2 failed with exit code $PHASE2_CODE" >&2
  exit "$PHASE2_CODE"
fi

if ! echo "$PHASE2_OUT" | grep -q "PHASE_2_COMPLETE"; then
  echo "[scenario] Phase 2 missing completion sentinel; nudging once..." >&2

  set +e
  PHASE2_OUT_2="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"Continue Phase 2 now.

Do NOT stop after the report_intent tool call. Keep using tools until:
- out/analysis_trends.md exists
- out/analysis_threads.json exists

Then output EXACTLY: PHASE_2_COMPLETE")"
  PHASE2_CODE_2=$?
  set -e

  echo "$PHASE2_OUT_2"
  PHASE2_OUT="$PHASE2_OUT"$'\n'"$PHASE2_OUT_2"

  if [[ "$PHASE2_CODE_2" != "0" && "$PHASE2_CODE_2" != "124" ]]; then
    echo "[scenario] Phase 2 retry failed with exit code $PHASE2_CODE_2" >&2
    exit "$PHASE2_CODE_2"
  fi

  if ! echo "$PHASE2_OUT" | grep -q "PHASE_2_COMPLETE"; then
    echo "[scenario] Phase 2 missing completion sentinel (continuing to validation)" >&2
  fi
fi

# Wait for child threads to finish (they may still be processing)
sleep 2

echo "[scenario] Validating Phase 2 output..."
PHASE2_VALIDATE_RETRIES="${SCENARIO_VALIDATE_RETRIES:-2}"
PHASE2_VALIDATE_ATTEMPT=0
while true; do
  set +e
  PHASE2_VALIDATE_OUT="$(bash "$SAMPLE_DIR/scripts/validate_phase2.sh" 2>&1)"
  PHASE2_VALIDATE_CODE=$?
  set -e

  if [[ "$PHASE2_VALIDATE_CODE" == "0" ]]; then
    echo "$PHASE2_VALIDATE_OUT"
    break
  fi

  echo "$PHASE2_VALIDATE_OUT" >&2

  if (( PHASE2_VALIDATE_ATTEMPT >= PHASE2_VALIDATE_RETRIES )); then
    echo "[scenario] Phase 2 validation failed after retries" >&2
    exit 1
  fi

  PHASE2_VALIDATE_ATTEMPT=$((PHASE2_VALIDATE_ATTEMPT + 1))
  PHASE2_VALIDATE_SNIP="$(printf "%s" "$PHASE2_VALIDATE_OUT" | head -c 1200)"

  echo "[scenario] Phase 2 validation failed; asking agent to fix outputs (attempt $PHASE2_VALIDATE_ATTEMPT/$PHASE2_VALIDATE_RETRIES)..." >&2

  set +e
  FIX2_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"Phase 2 outputs failed validation. Fix ONLY the files in out/ so that scripts/validate_phase2.sh passes.

Rules:
- First call report_intent
- Keep tool usage minimal

Validator output (truncated):
$PHASE2_VALIDATE_SNIP

When done, output EXACTLY: PHASE_2_COMPLETE")"
  FIX2_CODE=$?
  set -e

  echo "$FIX2_OUT"

  if [[ "$FIX2_CODE" != "0" && "$FIX2_CODE" != "124" ]]; then
    echo "[scenario] Phase 2 fix attempt failed with exit code $FIX2_CODE" >&2
    exit "$FIX2_CODE"
  fi
done

# ============================================================
# PHASE 3: Incident Report Generation
# ============================================================
echo ""
echo "========== PHASE 3: Incident Report =========="

set +e
PHASE3_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"You are finalizing the incident response. Follow these rules EXACTLY.

CONTEXT:
- Working directory: $SAMPLE_DIR
- Phases 1 & 2 completed successfully
- Now executing Phase 3: Final Incident Report

ALLOWED TOOLS: report_intent, read_text_file, write_text_file, execute_command, everything__echo

TOOL BUDGET: Maximum 15 tool calls for this phase.

PHASE 3 REQUIREMENTS (execute in order):
1. Report intent: {\"intent\": \"Phase 3: Incident Report\"}
2. Read all previous outputs:
   - out/phase1_validation.json
   - out/phase2_analysis.md
   - data/runbook.md
3. List all tool calls executed during this incident (search events if available, or summarize from context)
4. Create comprehensive incident report: out/incident_report.md
   Structure:
   # Incident Report: INC-2026-04-24-001
   
   ## Incident Summary
   - ID: INC-2026-04-24-001
   - Severity: Critical
   - Reported: 2026-04-24T00:35:00Z
   - Status: <Resolved/Ongoing>
   
   ## Timeline
   - Phase 1: Data Validation (completed)
   - Phase 2: Multi-Thread Analysis (completed)
   - Phase 3: Incident Report (in progress)
   
   ## Findings
   - Affected Sensors: sensor-001, sensor-003
   - Temperature Spike: <details from analysis>
   - Trend: <increasing/stable/decreasing>
   
   ## Impact Assessment
   - Data Centers affected
   - SLA status
   
   ## Mitigation Actions
   - Immediate actions taken
   - Follow-up required
   
   ## Tool Calls Executed
   - List all tools used during investigation
   
   ## Executive Summary
   - Root cause (if identified)
   - Current status
   - Next steps
5. Use MCP tool everything__echo with {\"message\": \"Incident response complete\"} as final checkpoint
6. Output EXACTLY: INCIDENT_RESPONSE_COMPLETE

FAILURE HANDLING:
- If you exceed tool budget, output: PHASE_3_BUDGET_EXCEEDED

NO natural language between tool calls. Only tool calls and the final status message.")"
PHASE3_CODE=$?
set -e

echo "$PHASE3_OUT"

if [[ "$PHASE3_CODE" != "0" && "$PHASE3_CODE" != "124" ]]; then
  echo "[scenario] Phase 3 failed with exit code $PHASE3_CODE" >&2
  exit "$PHASE3_CODE"
fi

if ! echo "$PHASE3_OUT" | grep -q "INCIDENT_RESPONSE_COMPLETE"; then
  echo "[scenario] Phase 3 missing completion sentinel; nudging once..." >&2

  set +e
  PHASE3_OUT_2="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"Continue Phase 3 now.

Do NOT stop after the report_intent tool call. Keep using tools until:
- out/final_report.md exists

Then output EXACTLY: INCIDENT_RESPONSE_COMPLETE")"
  PHASE3_CODE_2=$?
  set -e

  echo "$PHASE3_OUT_2"
  PHASE3_OUT="$PHASE3_OUT"$'\n'"$PHASE3_OUT_2"

  if [[ "$PHASE3_CODE_2" != "0" && "$PHASE3_CODE_2" != "124" ]]; then
    echo "[scenario] Phase 3 retry failed with exit code $PHASE3_CODE_2" >&2
    exit "$PHASE3_CODE_2"
  fi

  if ! echo "$PHASE3_OUT" | grep -q "INCIDENT_RESPONSE_COMPLETE"; then
    echo "[scenario] Phase 3 missing completion sentinel (continuing to validation)" >&2
  fi
fi

echo "[scenario] Validating Phase 3 output..."
PHASE3_VALIDATE_RETRIES="${SCENARIO_VALIDATE_RETRIES:-2}"
PHASE3_VALIDATE_ATTEMPT=0
while true; do
  set +e
  PHASE3_VALIDATE_OUT="$(bash "$SAMPLE_DIR/scripts/validate_phase3.sh" 2>&1)"
  PHASE3_VALIDATE_CODE=$?
  set -e

  if [[ "$PHASE3_VALIDATE_CODE" == "0" ]]; then
    echo "$PHASE3_VALIDATE_OUT"
    break
  fi

  echo "$PHASE3_VALIDATE_OUT" >&2

  if (( PHASE3_VALIDATE_ATTEMPT >= PHASE3_VALIDATE_RETRIES )); then
    echo "[scenario] Phase 3 validation failed after retries" >&2
    exit 1
  fi

  PHASE3_VALIDATE_ATTEMPT=$((PHASE3_VALIDATE_ATTEMPT + 1))
  PHASE3_VALIDATE_SNIP="$(printf "%s" "$PHASE3_VALIDATE_OUT" | head -c 1200)"

  echo "[scenario] Phase 3 validation failed; asking agent to fix outputs (attempt $PHASE3_VALIDATE_ATTEMPT/$PHASE3_VALIDATE_RETRIES)..." >&2

  set +e
  FIX3_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" prompt -s "$SESSION" \
"Phase 3 outputs failed validation. Fix ONLY the files in out/ so that scripts/validate_phase3.sh passes.

Rules:
- First call report_intent
- Keep tool usage minimal

Validator output (truncated):
$PHASE3_VALIDATE_SNIP

When done, output EXACTLY: INCIDENT_RESPONSE_COMPLETE")"
  FIX3_CODE=$?
  set -e

  echo "$FIX3_OUT"

  if [[ "$FIX3_CODE" != "0" && "$FIX3_CODE" != "124" ]]; then
    echo "[scenario] Phase 3 fix attempt failed with exit code $FIX3_CODE" >&2
    exit "$FIX3_CODE"
  fi
done

# ============================================================
# FINAL VERIFICATION
# ============================================================
echo ""
echo "========== FINAL VERIFICATION =========="

# Check all output files exist
required_outputs=(
  "out/phase1_validation.json"
  "out/phase2_analysis.md"
  "out/incident_report.md"
  "out/errors.log"
)

for output in "${required_outputs[@]}"; do
  if [[ ! -f "$SAMPLE_DIR/$output" ]]; then
    echo "[scenario] Missing required output: $output" >&2
    exit 1
  fi
done

echo "[scenario] All phases completed successfully ✓"
echo "[scenario] Outputs:"
ls -lh "$SAMPLE_DIR/out/"

exit 0
