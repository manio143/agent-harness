# SecurityOps Compliance Audit - Multi-Turn Agent Evaluation Scenario

## Overview

This scenario simulates a **SOC2 Type 2 compliance audit workflow** for evaluating the agent harness through manual log review. It exercises multi-turn reasoning, parallel thread analysis, error recovery from malformed data, and structured report generation across three phases.

**Scenario Type:** Manual evaluation (NOT an automated test)  
**Complexity:** High - Multi-phase, multi-thread, with intentional data corruption  
**Evaluation Method:** Manual log review + automated validation scripts

## Narrative

**Audit:** Quarterly SOC2 Type 2 compliance review (AUD-2026-Q2-001)

**Mission:** The agent must act as a security compliance auditor, following a structured audit runbook to:
1. Collect and validate server/user inventory data (handling malformed input gracefully)
2. Run parallel compliance checks using restricted child threads
3. Generate a risk-scored compliance report with remediation roadmap

**Challenge Points:**
- NDJSON parsing with intentional corruption requiring error recovery
- Multi-thread coordination with capability restrictions
- Policy-based violation detection (password age, MFA, patching)
- Risk scoring calculation and severity assessment
- Exemption handling (some violations have approved exceptions)

## Real-World Patterns

This scenario is grounded in actual security operations:
- **SOC2 audits:** Structured compliance frameworks with specific control requirements
- **IAM reviews:** User access certification, MFA enforcement, inactive account detection
- **Patch compliance:** Age-based vulnerability tracking, SLA enforcement
- **Parallel workstreams:** Complex audits split analysis tasks for efficiency

## Scenario Phases

### Phase 1: Data Collection & Validation (6-10 tool calls)
**Tools:** `report_intent`, `read_text_file`, `write_text_file`, `execute_command`, `everything__echo`

**Objectives:**
- Load audit metadata (`data/audit_metadata.json`)
- Parse server inventory (`data/servers.csv`) - 10 servers
- Parse user data (`data/users.ndjson`) - 10 users, NDJSON format
- **INTENTIONAL FAILURE:** Attempt to parse `data/users_malformed.ndjson`
- Recover gracefully: log errors, continue with valid data
- Generate inventory summary (`out/phase1_inventory.json`)

**Success Criteria:**
- ✅ Malformed NDJSON error detected and logged
- ✅ Error written to `out/errors.log`
- ✅ Inventory JSON has correct server count (10) and user count (10)
- ✅ Data quality section documents the malformed file issue

### Phase 2: Policy Compliance Checks (12-18 tool calls)
**Tools:** `report_intent`, `thread_start`, `thread_send`, `thread_read`, `thread_list`, `read_text_file`, `write_text_file`

**Objectives:**
- Create TWO child threads (mode: `single`) for parallel analysis:
  - **Thread A (audit-users):** Check password age, MFA status, account activity
  - **Thread B (audit-servers):** Check patch age, SSH port compliance
- Each thread has restricted capabilities: only `read_text_file`, `write_text_file`
- Read results from both threads via `thread_read`
- Consider exemptions from `data/exemptions.md`

**Expected Violations:**
- **Users:**
  - `carol`: MFA disabled with admin access, password 120 days old
  - `frank`: MFA disabled, inactive since January, password 200 days old
  - `test-admin`: MFA disabled, inactive, password 400 days old, expired exemption
- **Servers:**
  - `api-prod-02`: Last patched 2026-01-10 (>90 days)
  - `db-prod-02`: Last patched 2026-02-20 (>60 days)

**Success Criteria:**
- ✅ Both threads created with `mode: "single"`
- ✅ Capability restrictions enforced (children can't use execute_command)
- ✅ `out/phase2_user_findings.json` has ≥3 violations
- ✅ `out/phase2_server_findings.json` has ≥2 violations
- ✅ Critical users (carol, frank, test-admin) flagged

### Phase 3: Risk Assessment & Reporting (8-12 tool calls)
**Tools:** `report_intent`, `read_text_file`, `write_text_file`, `everything__echo`

**Objectives:**
- Aggregate all findings from Phase 1 & 2
- Calculate compliance score (100 - severity deductions)
- Generate comprehensive report (`out/compliance_report.md`)
- Include: Executive Summary, Compliance Score, Findings, Remediation Roadmap
- MCP checkpoint via `everything__echo`

**Success Criteria:**
- ✅ Report contains all required sections
- ✅ Audit ID (AUD-2026-Q2-001) referenced
- ✅ Compliance score/percentage calculated
- ✅ Critical/High findings documented
- ✅ Report >150 words (substantive, not stub)

## Dataset

### Valid Data Files
| File | Format | Size | Description |
|------|--------|------|-------------|
| `servers.csv` | CSV | ~700B | 10 production servers with patch dates |
| `users.ndjson` | NDJSON | ~1.8KB | 10 IAM users (humans + service accounts) |
| `compliance_policy.yaml` | YAML | ~1.1KB | SOC2 requirements and thresholds |
| `audit_metadata.json` | JSON | ~660B | Audit scope and configuration |
| `audit_runbook.md` | Markdown | ~2.4KB | Detailed audit procedures |
| `exemptions.md` | Markdown | ~1.2KB | Approved policy exemptions |

### Malformed Data (Intentional)
| File | Issues |
|------|--------|
| `users_malformed.ndjson` | Invalid JSON syntax, missing quotes, incomplete objects, garbage text |

## Tool Coverage

**Threading (Phase 2):**
- `thread_start` (single mode, capability restrictions)
- `thread_read` (reading child outputs)
- `thread_list` (verifying thread creation)

**Host File System:**
- `read_text_file` (CSV, NDJSON, YAML, JSON, Markdown)
- `write_text_file` (JSON findings, Markdown reports, error logs)

**Host Execution:**
- `execute_command` (optional, for data processing)

**MCP Integration:**
- `everything__echo` (audit checkpoints)

**Intent Tracking:**
- `report_intent` (phase transitions)

## Running the Scenario

### Prerequisites
```bash
cd /home/node/.openclaw/workspace/marian-agent
dotnet build Agent.slnx -c Release
```

### Execution
```bash
bash samples/SecurityOps.ComplianceAudit/run.sh \
  "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll"
```

### Environment Variables
```bash
export ACP_TIMEOUT=300           # ACP operation timeout
export ACPX_TTL=300              # Queue owner idle TTL
export ACPX_WALL_TIMEOUT=240     # Hard wall-time limit
export ACP_PROMPT_RETRIES=2      # Retry count for failures
```

## Evaluation Guide

### Where to Look in Logs

**Session Events:**
```bash
# Main thread
cat .agent/sessions/<session_id>/threads/main/events.jsonl | jq -c 'select(.type | startswith("tool_"))'

# Child threads (Phase 2)
cat .agent/sessions/<session_id>/threads/audit-users-*/events.jsonl
cat .agent/sessions/<session_id>/threads/audit-servers-*/events.jsonl
```

### What to Evaluate

**Error Recovery (Phase 1):**
- Did agent detect NDJSON parsing failure?
- Was error logged to `out/errors.log`?
- Did execution continue with valid data?

**Thread Coordination (Phase 2):**
- Were both threads created with `mode: "single"`?
- Were capability restrictions correct (`deny: ["*"]`, `allow: [...]`)?
- Did agent use `thread_read` to fetch results?

**Compliance Analysis:**
- Were the expected violations detected?
- Did agent reference exemptions.md for valid exceptions?
- Is the severity assignment reasonable?

**Report Quality (Phase 3):**
- Is the compliance score calculated correctly?
- Are remediation priorities logical?
- Is the executive summary substantive?

## Scoring Rubric

### Execution Quality (40 points)
- [ ] All 3 phases completed (10 pts)
- [ ] No runtime errors/exceptions (10 pts)
- [ ] Stayed within tool budgets (10 pts)
- [ ] Correct phase sequencing (10 pts)

### Tool Precision (30 points)
- [ ] `thread_start` with correct `mode` parameter (10 pts)
- [ ] Capability restrictions properly set (5 pts)
- [ ] NDJSON parsing handled correctly (5 pts)
- [ ] MCP tools invoked successfully (5 pts)
- [ ] `report_intent` at phase boundaries (5 pts)

### Error Recovery (15 points)
- [ ] Malformed NDJSON error detected (5 pts)
- [ ] Error logged to file (5 pts)
- [ ] Execution continued gracefully (5 pts)

### Output Quality (15 points)
- [ ] Inventory JSON well-formed with correct counts (5 pts)
- [ ] Findings detect expected violations (5 pts)
- [ ] Compliance report comprehensive (>150 words) (5 pts)

**Total: 100 points**

## Common Failure Modes

1. **NDJSON parsing confusion:** Agent may try JSON.parse() on entire file instead of line-by-line
2. **Missing `mode` in thread_start:** Common parameter omission
3. **Capability syntax errors:** Incorrect `deny`/`allow` structure
4. **Skipping thread_read:** Agent assumes inline results instead of explicit read
5. **Exemption oversight:** Flagging violations that have valid exemptions
6. **Risk score miscalculation:** Not following the severity-to-points mapping

## Files

```
SecurityOps.ComplianceAudit/
├── README.md                       # This file
├── IMPLEMENTATION_SUMMARY.md       # Implementation notes
├── run.sh                          # Main scenario executor
├── .acpxrc.json                    # MCP configuration
├── data/
│   ├── servers.csv                 # Server inventory (10 servers)
│   ├── users.ndjson                # User data (10 users, NDJSON)
│   ├── users_malformed.ndjson      # Intentionally broken NDJSON
│   ├── compliance_policy.yaml      # SOC2 requirements
│   ├── audit_metadata.json         # Audit configuration
│   ├── audit_runbook.md            # Audit procedures
│   └── exemptions.md               # Approved policy exemptions
├── scripts/
│   ├── validate_phase1.sh          # Inventory validation
│   ├── validate_phase2.sh          # Findings validation
│   └── validate_phase3.sh          # Report validation
└── out/                            # Generated during execution
    ├── phase1_inventory.json
    ├── phase2_user_findings.json
    ├── phase2_server_findings.json
    ├── compliance_report.md
    └── errors.log
```

## License

Part of the marian-agent evaluation suite.
