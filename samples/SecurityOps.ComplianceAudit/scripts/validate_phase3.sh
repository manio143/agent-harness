#!/usr/bin/env bash
# Phase 3 Validation: Risk assessment and final compliance report
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SAMPLE_DIR/out"

echo "[validate_phase3] Checking Phase 3 outputs..."

# Final report must exist
if [[ ! -f "$OUT_DIR/compliance_report.md" ]]; then
  echo "FAIL: compliance_report.md not found" >&2
  exit 1
fi

REPORT="$OUT_DIR/compliance_report.md"

# Required sections
required_sections=(
  "Executive Summary"
  "Compliance Score"
  "Critical"
  "Findings"
  "Remediation"
)

for section in "${required_sections[@]}"; do
  if ! grep -qi "$section" "$REPORT"; then
    echo "FAIL: Missing section '$section' in compliance report" >&2
    exit 1
  fi
done

# Must mention the audit ID
if ! grep -q "AUD-2026-Q2-001" "$REPORT"; then
  echo "FAIL: Audit ID not found in report" >&2
  exit 1
fi

# Must flag at least one critical/high finding
if ! grep -qiE "(critical|high)\s*(severity|finding|risk|:)" "$REPORT"; then
  echo "FAIL: No critical or high severity findings documented" >&2
  exit 1
fi

# Must mention key violations
key_items=(
  "MFA"
  "password"
  "patch"
)

found_items=0
for item in "${key_items[@]}"; do
  if grep -qi "$item" "$REPORT"; then
    ((found_items++))
  fi
done

if [[ "$found_items" -lt 2 ]]; then
  echo "FAIL: Report missing key compliance topics (MFA, password, patching)" >&2
  exit 1
fi

# Verify substantive report (not stub)
word_count=$(wc -w < "$REPORT")
if [[ "$word_count" -lt 150 ]]; then
  echo "FAIL: Report too short (${word_count} words, expected >150)" >&2
  exit 1
fi

# Check for risk score or compliance percentage
if ! grep -qE "[0-9]+\s*(%|percent|score|/100)" "$REPORT"; then
  echo "FAIL: No compliance score or percentage found" >&2
  exit 1
fi

echo "[validate_phase3] Phase 3 validation passed ✓"
exit 0
