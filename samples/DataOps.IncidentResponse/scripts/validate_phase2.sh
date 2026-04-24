#!/usr/bin/env bash
# Phase 2 Validation: Thread-based analysis and statistics
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SAMPLE_DIR/out"

echo "[validate_phase2] Checking Phase 2 outputs..."

# Check that analysis output exists
if [[ ! -f "$OUT_DIR/phase2_analysis.md" ]]; then
  echo "FAIL: phase2_analysis.md not found" >&2
  exit 1
fi

ANALYSIS="$OUT_DIR/phase2_analysis.md"

# Check for required sections in markdown
required_sections=(
  "Sensor Analysis"
  "sensor-001"
  "sensor-003"
  "Statistics"
  "Recommendations"
)

for section in "${required_sections[@]}"; do
  if ! grep -qi "$section" "$ANALYSIS"; then
    echo "FAIL: Missing section '$section' in analysis" >&2
    exit 1
  fi
done

# Check that statistics are present (look for numeric patterns)
if ! grep -qE "(Min|Max|Average|Mean).*[0-9]+\.[0-9]+" "$ANALYSIS"; then
  echo "FAIL: No statistics found in analysis" >&2
  exit 1
fi

# Check for temperature trends
if ! grep -qiE "(increasing|rising|spike|upward)" "$ANALYSIS"; then
  echo "FAIL: No trend analysis found for temperature spike" >&2
  exit 1
fi

# Verify word count (should be substantive, not just a stub)
word_count=$(wc -w < "$ANALYSIS")
if [[ "$word_count" -lt 100 ]]; then
  echo "FAIL: Analysis too short (${word_count} words, expected >100)" >&2
  exit 1
fi

echo "[validate_phase2] Phase 2 analysis output is correct ✓"
exit 0
