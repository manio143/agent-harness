#!/usr/bin/env bash
# Phase 2 Validation: Policy compliance checks (threading validation)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SAMPLE_DIR/out"

echo "[validate_phase2] Checking Phase 2 outputs..."

# Both thread outputs must exist
for file in "phase2_user_findings.json" "phase2_server_findings.json"; do
  if [[ ! -f "$OUT_DIR/$file" ]]; then
    echo "FAIL: $file not found (child thread output missing)" >&2
    exit 1
  fi
done

# Validate user findings structure
python3 - "$OUT_DIR/phase2_user_findings.json" <<'PYTHON'
import json, sys

with open(sys.argv[1], 'r') as f:
    findings = json.load(f)

# Must have findings list
if 'findings' not in findings and 'violations' not in findings:
    # Allow top-level to be a list
    if not isinstance(findings, list):
        print("FAIL: User findings must contain 'findings' or 'violations' or be a list", file=sys.stderr)
        sys.exit(1)
    items = findings
else:
    items = findings.get('findings', findings.get('violations', []))

# Must identify at least 3 issues:
# - carol: MFA disabled for admin user, password >90 days
# - frank: MFA disabled, inactive account, password >90 days
# - test-admin: MFA disabled, inactive, password >90 days, expired exemption
violations_found = 0
for item in items if isinstance(items, list) else []:
    if isinstance(item, dict):
        violations_found += 1
    
if violations_found < 3:
    print(f"FAIL: Expected at least 3 user violations, found {violations_found}", file=sys.stderr)
    sys.exit(1)

# Check for critical users that MUST be flagged
critical_users = {'carol', 'frank', 'test-admin'}
flagged_users = set()
for item in items if isinstance(items, list) else []:
    if isinstance(item, dict):
        user = item.get('user', item.get('username', item.get('user_id', '')))
        if isinstance(user, str):
            flagged_users.add(user.lower())

# At least 2 of the critical users must be flagged
matched = critical_users & flagged_users
if len(matched) < 2:
    print(f"FAIL: Expected to flag at least 2 of {critical_users}, found: {flagged_users}", file=sys.stderr)
    sys.exit(1)

print(f"PASS: User findings validated ({violations_found} violations, flagged: {matched})")
PYTHON

# Validate server findings
python3 - "$OUT_DIR/phase2_server_findings.json" <<'PYTHON'
import json, sys

with open(sys.argv[1], 'r') as f:
    findings = json.load(f)

# Must have findings
if 'findings' not in findings and 'violations' not in findings:
    if not isinstance(findings, list):
        print("FAIL: Server findings must contain 'findings' or 'violations' or be a list", file=sys.stderr)
        sys.exit(1)
    items = findings
else:
    items = findings.get('findings', findings.get('violations', []))

# Must identify patching violations:
# - api-prod-02: last_patched 2026-01-10 (>90 days old)
# - db-prod-02: last_patched 2026-02-20 (>60 days old)
violations_found = 0
for item in items if isinstance(items, list) else []:
    if isinstance(item, dict):
        violations_found += 1

if violations_found < 2:
    print(f"FAIL: Expected at least 2 server violations, found {violations_found}", file=sys.stderr)
    sys.exit(1)

# Check for servers that must be flagged for patching
flagged_servers = set()
for item in items if isinstance(items, list) else []:
    if isinstance(item, dict):
        server = item.get('server', item.get('hostname', item.get('host', '')))
        if isinstance(server, str):
            flagged_servers.add(server.lower())

critical_servers = {'api-prod-02', 'db-prod-02'}
matched = critical_servers & flagged_servers
if len(matched) < 1:
    print(f"FAIL: Expected to flag at least 1 of {critical_servers}, found: {flagged_servers}", file=sys.stderr)
    sys.exit(1)

print(f"PASS: Server findings validated ({violations_found} violations, flagged: {matched})")
PYTHON

echo "[validate_phase2] Phase 2 validation passed ✓"
exit 0
