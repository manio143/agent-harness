#!/usr/bin/env bash
# Phase 1 Validation: Data collection and inventory validation
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SAMPLE_DIR/out"

echo "[validate_phase1] Checking Phase 1 outputs..."

# Check inventory output exists
if [[ ! -f "$OUT_DIR/phase1_inventory.json" ]]; then
  echo "FAIL: phase1_inventory.json not found" >&2
  exit 1
fi

# Validate JSON structure
python3 - "$OUT_DIR/phase1_inventory.json" "$SAMPLE_DIR/data/servers.csv" "$SAMPLE_DIR/data/users.ndjson" <<'PYTHON'
import json, sys, csv

inventory_file = sys.argv[1]
servers_file = sys.argv[2]
users_file = sys.argv[3]

with open(inventory_file, 'r') as f:
    inv = json.load(f)

# Required top-level fields
required = ['audit_id', 'collected_at', 'servers', 'users', 'data_quality']
missing = [f for f in required if f not in inv]
if missing:
    print(f"FAIL: Missing required fields: {missing}", file=sys.stderr)
    sys.exit(1)

# Count expected servers
with open(servers_file, 'r') as f:
    reader = csv.DictReader(f)
    expected_servers = sum(1 for _ in reader)

# Count expected users (valid NDJSON lines)
expected_users = 0
with open(users_file, 'r') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                json.loads(line)
                expected_users += 1
            except json.JSONDecodeError:
                pass  # malformed lines don't count

# Validate server count
servers_info = inv.get('servers', {})
if isinstance(servers_info, dict):
    server_count = servers_info.get('count', servers_info.get('total', 0))
elif isinstance(servers_info, list):
    server_count = len(servers_info)
else:
    server_count = 0

if server_count != expected_servers:
    print(f"FAIL: Server count mismatch: expected {expected_servers}, got {server_count}", file=sys.stderr)
    sys.exit(1)

# Validate user count (should match valid records)
users_info = inv.get('users', {})
if isinstance(users_info, dict):
    user_count = users_info.get('count', users_info.get('total', 0))
elif isinstance(users_info, list):
    user_count = len(users_info)
else:
    user_count = 0

if user_count != expected_users:
    print(f"FAIL: User count mismatch: expected {expected_users}, got {user_count}", file=sys.stderr)
    sys.exit(1)

# Check data quality section exists
dq = inv.get('data_quality', {})
if 'malformed_records' not in dq and 'errors' not in dq and 'issues' not in dq:
    print(f"FAIL: Data quality section missing error tracking", file=sys.stderr)
    sys.exit(1)

print(f"PASS: Inventory validated (servers={server_count}, users={user_count})")
PYTHON

# Check error log exists (malformed data should have been logged)
if [[ ! -f "$OUT_DIR/errors.log" ]]; then
  echo "FAIL: errors.log not found (malformed data should have been logged)" >&2
  exit 1
fi

# Verify error log contains mention of malformed data
if ! grep -qi "malformed\|error\|invalid\|failed" "$OUT_DIR/errors.log"; then
  echo "FAIL: errors.log should contain error mentions" >&2
  exit 1
fi

echo "[validate_phase1] Phase 1 validation passed ✓"
exit 0
