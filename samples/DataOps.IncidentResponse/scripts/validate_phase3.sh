#!/usr/bin/env bash
# Phase 3 Validation: Final incident report
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SAMPLE_DIR/out"

echo "[validate_phase3] Checking Phase 3 outputs..."

# Check that incident report exists
if [[ ! -f "$OUT_DIR/incident_report.md" ]]; then
  echo "FAIL: incident_report.md not found" >&2
  exit 1
fi

REPORT="$OUT_DIR/incident_report.md"

# Required sections for incident report
required_sections=(
  "Incident Summary"
  "INC-2026-04-24-001"
  "Timeline"
  "Findings"
  "Impact"
  "Mitigation"
  "Tool Calls"
)

for section in "${required_sections[@]}"; do
  if ! grep -qi "$section" "$REPORT"; then
    echo "FAIL: Missing section '$section' in report" >&2
    exit 1
  fi
done

# Verify tool calls are documented
tool_names=(
  "report_intent"
  "thread_start"
  "read_text_file"
  "write_text_file"
)

found_tools=0
for tool in "${tool_names[@]}"; do
  if grep -qi "$tool" "$REPORT"; then
    ((found_tools++))
  fi
done

if [[ "$found_tools" -lt 3 ]]; then
  echo "FAIL: Expected at least 3 tool calls documented, found $found_tools" >&2
  exit 1
fi

# Check executive summary exists and has substance
if ! grep -qiE "executive summary|summary" "$REPORT"; then
  echo "FAIL: No executive summary found" >&2
  exit 1
fi

# Verify report length (should be comprehensive)
word_count=$(wc -w < "$REPORT")
if [[ "$word_count" -lt 200 ]]; then
  echo "FAIL: Report too short (${word_count} words, expected >200)" >&2
  exit 1
fi

echo "[validate_phase3] Phase 3 incident report is correct ✓"
exit 0
