#!/usr/bin/env bash
# SecurityOps Compliance Audit - Multi-Turn Agent Evaluation Scenario
# Tests: multi-phase workflow, thread coordination, error recovery, compliance analysis
set -euo pipefail

AGENT_CMD="${1:?usage: $0 <agent_cmd>}"
SESSION="secops-$(date +%s)"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$SCRIPT_DIR"

# User-secrets (including Groq config) are loaded only when DOTNET_ENVIRONMENT=Development.
# For sample runs we default to Development unless the caller overrides it.
if [[ -z "${DOTNET_ENVIRONMENT:-}" ]]; then
  export DOTNET_ENVIRONMENT=Development
fi

# We run the agent with --cwd "$SAMPLE_DIR" so relative paths like data/*.csv work.
# That would break a relative Agent.Server.dll path passed from the repo root, so normalize it.
REPO_ROOT="$(cd "$SAMPLE_DIR/../.." && pwd)"
read -r -a _agent_words <<<"$AGENT_CMD"
if [[ "${_agent_words[0]:-}" == "dotnet" && "${_agent_words[1]:-}" != "" && "${_agent_words[1]}" != /* ]]; then
  _agent_words[1]="$REPO_ROOT/${_agent_words[1]}"
  AGENT_CMD="${_agent_words[*]}"
fi
unset _agent_words

echo "[scenario] SecurityOps Compliance Audit - Session: $SESSION"

# Create new session
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

# Clean previous outputs
rm -f "$SAMPLE_DIR/out/"*.json "$SAMPLE_DIR/out/"*.md "$SAMPLE_DIR/out/"*.log 2>/dev/null || true

# ============================================================
# PHASE 1: Data Collection & Validation
# ============================================================
echo ""
echo "========== PHASE 1: Data Collection & Validation =========="

set +e
PHASE1_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" --allowed-tools "report_intent,read_text_file,write_text_file,execute_command,everything__echo" prompt -s "$SESSION" \
"You are a security compliance agent performing a SOC2 audit.

CONTEXT:
- Working directory: $SAMPLE_DIR
- Audit ID: AUD-2026-Q2-001
- Task: Phase 1 - Collect and validate audit data

ALLOWED TOOLS: report_intent, read_text_file, write_text_file, execute_command, everything__echo

TOOL BUDGET: Maximum 15 tool calls.

OBJECTIVES:
1. Report your intent for this phase
2. Load the audit metadata from data/audit_metadata.json
3. Read server inventory from data/servers.csv
4. Read user data from data/users.ndjson (NDJSON format - one JSON object per line)
5. ATTEMPT to read data/users_malformed.ndjson - this file contains invalid JSON
   - When parsing fails, log the error and continue with valid data only
   - Write any errors to out/errors.log
6. Read compliance policy from data/compliance_policy.yaml
7. Create inventory summary: out/phase1_inventory.json
   Include: audit_id, collected_at, servers (count), users (count), data_quality (malformed records noted)

OUTPUT REQUIREMENTS:
- out/phase1_inventory.json with server count, user count, audit_id, and data_quality section
- out/errors.log with any parsing errors from malformed data

When complete, output: PHASE_1_COMPLETE

If you exceed tool budget: PHASE_1_BUDGET_EXCEEDED
If critical failure occurs: PHASE_1_FAILED")"
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
  PHASE1_OUT_2="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" --allowed-tools "report_intent,read_text_file,write_text_file,execute_command,everything__echo" prompt -s "$SESSION" \
"Continue Phase 1 now.

Do NOT stop after the report_intent tool call. Keep using tools until:
- out/phase1_inventory.json exists and contains a summary (audit_id, server/user counts, data_quality)
- out/errors.log exists (write parsing errors from users_malformed.ndjson)

Then output exactly: PHASE_1_COMPLETE")"
  PHASE1_CODE_2=$?
  set -e

  echo "$PHASE1_OUT_2"
  PHASE1_OUT="$PHASE1_OUT"$'\n'"$PHASE1_OUT_2"

  if [[ "$PHASE1_CODE_2" != "0" && "$PHASE1_CODE_2" != "124" ]]; then
    echo "[scenario] Phase 1 retry failed with exit code $PHASE1_CODE_2" >&2
    exit "$PHASE1_CODE_2"
  fi

  if ! echo "$PHASE1_OUT" | grep -q "PHASE_1_COMPLETE"; then
    echo "[scenario] Phase 1 did not complete successfully" >&2
    exit 1
  fi
fi

echo "[scenario] Validating Phase 1 output..."
bash "$SAMPLE_DIR/scripts/validate_phase1.sh"

# ============================================================
# PHASE 2: Policy Compliance Checks (Parallel Threads)
# ============================================================
echo ""
echo "========== PHASE 2: Policy Compliance Checks =========="

set +e
PHASE2_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" --allowed-tools "report_intent,thread_start,thread_send,thread_read,thread_list,read_text_file,write_text_file" prompt -s "$SESSION" \
"You are continuing the SOC2 compliance audit.

CONTEXT:
- Working directory: $SAMPLE_DIR
- Phase 1 completed - inventory collected
- Task: Phase 2 - Run compliance checks using parallel analysis threads

ALLOWED TOOLS: report_intent, thread_start, thread_send, thread_read, thread_list, read_text_file, write_text_file

TOOL BUDGET: Maximum 20 tool calls.

OBJECTIVES:
1. Report your intent for this phase
2. Read the inventory from out/phase1_inventory.json
3. Read compliance policy from data/compliance_policy.yaml
4. Create TWO child threads (mode: single) to analyze compliance in parallel:

   Thread A - User Compliance (name: audit-users):
   Message: Check user compliance against policy. Read data/users.ndjson and data/compliance_policy.yaml. 
   For each user, check: password age (<90 days), MFA status (required for admin/prod-access groups except service accounts), 
   account activity (flag inactive >90 days). Output findings to out/phase2_user_findings.json with structure:
   {\"findings\": [{\"user\": \"username\", \"violation\": \"description\", \"severity\": \"critical|high|medium|low\"}]}
   Capabilities: allow read_text_file, write_text_file only

   Thread B - Server Compliance (name: audit-servers):
   Message: Check server compliance against policy. Read data/servers.csv and data/compliance_policy.yaml.
   For each server, check: patch age (<60 days, critical <14 days), SSH port compliance.
   Output findings to out/phase2_server_findings.json with same structure as user findings.
   Capabilities: allow read_text_file, write_text_file only

5. Use thread_list to verify both threads exist
6. Read results from both threads using thread_read
7. Read exemptions from data/exemptions.md - some violations may have approved exemptions

OUTPUT REQUIREMENTS:
- Both child threads must produce their output files
- Threads must be created with mode: single and restricted capabilities

When complete, output: PHASE_2_COMPLETE

If thread creation fails, retry once with corrected parameters.
If you exceed tool budget: PHASE_2_BUDGET_EXCEEDED")"
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
  PHASE2_OUT_2="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" --allowed-tools "report_intent,thread_start,thread_send,thread_read,thread_list,read_text_file,write_text_file" prompt -s "$SESSION" \
"Continue Phase 2 now.

Do NOT stop after the report_intent tool call. Keep using tools until:
- out/phase2_user_findings.json exists
- out/phase2_server_findings.json exists
- out/thread_log.md exists and references both child threads

Then output exactly: PHASE_2_COMPLETE")"
  PHASE2_CODE_2=$?
  set -e

  echo "$PHASE2_OUT_2"
  PHASE2_OUT="$PHASE2_OUT"$'\n'"$PHASE2_OUT_2"

  if [[ "$PHASE2_CODE_2" != "0" && "$PHASE2_CODE_2" != "124" ]]; then
    echo "[scenario] Phase 2 retry failed with exit code $PHASE2_CODE_2" >&2
    exit "$PHASE2_CODE_2"
  fi

  if ! echo "$PHASE2_OUT" | grep -q "PHASE_2_COMPLETE"; then
    echo "[scenario] Phase 2 did not complete successfully" >&2
    exit 1
  fi
fi

# Allow child threads time to complete
sleep 2

echo "[scenario] Validating Phase 2 output..."
bash "$SAMPLE_DIR/scripts/validate_phase2.sh"

# ============================================================
# PHASE 3: Risk Assessment & Final Report
# ============================================================
echo ""
echo "========== PHASE 3: Risk Assessment & Report =========="

set +e
PHASE3_OUT="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" --allowed-tools "report_intent,read_text_file,write_text_file,everything__echo" prompt -s "$SESSION" \
"You are finalizing the SOC2 compliance audit.

CONTEXT:
- Working directory: $SAMPLE_DIR
- Phases 1 & 2 complete - findings collected
- Task: Phase 3 - Generate risk assessment and final compliance report

ALLOWED TOOLS: report_intent, read_text_file, write_text_file, everything__echo

TOOL BUDGET: Maximum 15 tool calls.

OBJECTIVES:
1. Report your intent for this phase
2. Read all previous phase outputs:
   - out/phase1_inventory.json
   - out/phase2_user_findings.json
   - out/phase2_server_findings.json
3. Read the audit runbook: data/audit_runbook.md
4. Calculate compliance score based on findings severity:
   - Start at 100, deduct: critical=-15, high=-10, medium=-5, low=-2
5. Generate comprehensive compliance report: out/compliance_report.md
   Structure:
   # SOC2 Compliance Report: AUD-2026-Q2-001
   
   ## Executive Summary
   - Audit scope, date, compliance score/percentage
   - Overall assessment (pass/fail/conditional)
   
   ## Compliance Score
   - Starting score: 100
   - Deductions itemized
   - Final score: X/100 (X%)
   
   ## Critical & High Findings
   - List all critical/high severity issues requiring immediate action
   
   ## All Findings Summary
   - Categorized by severity and type (user vs server)
   
   ## Remediation Roadmap
   - Priority 1 (immediate): critical issues
   - Priority 2 (7 days): high issues
   - Priority 3 (30 days): medium issues
   
   ## Appendix
   - Data sources reviewed
   - Exemptions applied
   
6. Use everything__echo with message \"Audit complete\" as checkpoint

When complete, output: AUDIT_COMPLETE

If you exceed tool budget: PHASE_3_BUDGET_EXCEEDED")"
PHASE3_CODE=$?
set -e

echo "$PHASE3_OUT"

if [[ "$PHASE3_CODE" != "0" && "$PHASE3_CODE" != "124" ]]; then
  echo "[scenario] Phase 3 failed with exit code $PHASE3_CODE" >&2
  exit "$PHASE3_CODE"
fi

if ! echo "$PHASE3_OUT" | grep -q "AUDIT_COMPLETE"; then
  echo "[scenario] Phase 3 missing completion sentinel; nudging once..." >&2

  set +e
  PHASE3_OUT_2="$(timeout "${ACPX_WALL_TIMEOUT:-240}" acpx --approve-all --non-interactive-permissions fail --ttl "${ACPX_TTL:-300}" --prompt-retries "${ACP_PROMPT_RETRIES:-2}" --agent "$AGENT_CMD" --cwd "$SAMPLE_DIR" --timeout "${ACP_TIMEOUT:-300}" --allowed-tools "report_intent,read_text_file,write_text_file,everything__echo" prompt -s "$SESSION" \
"Continue Phase 3 now.

Do NOT stop after the report_intent tool call. Keep using tools until:
- out/compliance_report.md exists (executive summary + findings + remediation roadmap)
- out/phase3_risk_register.json exists (risk-scored items)

Then output exactly: AUDIT_COMPLETE")"
  PHASE3_CODE_2=$?
  set -e

  echo "$PHASE3_OUT_2"
  PHASE3_OUT="$PHASE3_OUT"$'\n'"$PHASE3_OUT_2"

  if [[ "$PHASE3_CODE_2" != "0" && "$PHASE3_CODE_2" != "124" ]]; then
    echo "[scenario] Phase 3 retry failed with exit code $PHASE3_CODE_2" >&2
    exit "$PHASE3_CODE_2"
  fi

  if ! echo "$PHASE3_OUT" | grep -q "AUDIT_COMPLETE"; then
    echo "[scenario] Phase 3 did not complete successfully" >&2
    exit 1
  fi
fi

echo "[scenario] Validating Phase 3 output..."
bash "$SAMPLE_DIR/scripts/validate_phase3.sh"

# ============================================================
# FINAL VERIFICATION
# ============================================================
echo ""
echo "========== FINAL VERIFICATION =========="

required_outputs=(
  "out/phase1_inventory.json"
  "out/phase2_user_findings.json"
  "out/phase2_server_findings.json"
  "out/compliance_report.md"
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
