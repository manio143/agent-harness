#!/usr/bin/env bash
# Phase 1 Validation: Data integrity and anomaly detection
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SAMPLE_DIR/out"

echo "[validate_phase1] Checking Phase 1 outputs..."

# Check that validation output exists
if [[ ! -f "$OUT_DIR/phase1_validation.json" ]]; then
  echo "FAIL: phase1_validation.json not found" >&2
  exit 1
fi

# Validate JSON structure using Python
python3 - "$OUT_DIR/phase1_validation.json" <<'PYTHON'
import json, sys, os

validation_file = sys.argv[1]
with open(validation_file, 'r') as f:
    data = json.load(f)

# Required top-level fields
required_fields = ['incident_id', 'validation_timestamp', 'data_sources', 'anomalies', 'status_summary']
missing = [f for f in required_fields if f not in data]
if missing:
    print(f"FAIL: Missing required fields: {missing}", file=sys.stderr)
    sys.exit(1)

# Check anomalies structure
if 'critical_sensors' not in data['anomalies']:
    print("FAIL: Missing 'critical_sensors' in anomalies", file=sys.stderr)
    sys.exit(1)

if 'warning_sensors' not in data['anomalies']:
    print("FAIL: Missing 'warning_sensors' in anomalies", file=sys.stderr)
    sys.exit(1)

# Verify critical sensors match incident report
expected_critical = {'sensor-001', 'sensor-003'}
found_critical = set(data['anomalies']['critical_sensors'])

if not expected_critical.issubset(found_critical):
    print(f"FAIL: Expected critical sensors {expected_critical}, found {found_critical}", file=sys.stderr)
    sys.exit(1)

# Check status summary has counts
summary = data.get('status_summary', {})
required_statuses = ['ok', 'warning', 'alert', 'critical']
for status in required_statuses:
    if status not in summary:
        print(f"FAIL: Missing status count for '{status}'", file=sys.stderr)
        sys.exit(1)
    if not isinstance(summary[status], int):
        print(f"FAIL: Status count for '{status}' is not an integer", file=sys.stderr)
        sys.exit(1)

# Verify critical count is >0 (we know there are critical readings)
if summary['critical'] < 1:
    print(f"FAIL: Expected at least 1 critical reading, got {summary['critical']}", file=sys.stderr)
    sys.exit(1)

print("PASS: Phase 1 validation output is correct")
PYTHON

echo "[validate_phase1] Phase 1 validation passed ✓"
exit 0
