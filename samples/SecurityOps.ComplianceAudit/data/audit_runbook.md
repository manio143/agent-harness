# Security Compliance Audit Runbook

## Audit Overview

This runbook guides the quarterly SOC2 compliance audit process. Each phase builds
on the previous findings to create a comprehensive compliance assessment.

## Phase 1: Data Collection & Validation

### Objectives
- Gather current server and user inventories
- Validate data integrity
- Flag any data quality issues for manual review

### Required Inputs
- `servers.csv` - Current server inventory
- `users.ndjson` - IAM user export
- `compliance_policy.yaml` - Active compliance requirements

### Expected Output
- Validated inventory counts
- Data quality report
- Any parsing errors logged

---

## Phase 2: Policy Compliance Checks

### Objectives
- Check password policy compliance
- Verify MFA enforcement
- Audit patching status
- Review access controls

### Key Checks
1. **Password Age**: All users must change passwords within 90 days
2. **MFA Status**: Required for admin and prod-access groups
3. **Patch Level**: Servers patched within 60 days (critical: 14 days)
4. **Inactive Accounts**: Flag accounts inactive >90 days

### Parallel Analysis
For efficiency, split checks into parallel workstreams:
- **Workstream A**: User compliance (password, MFA, inactive)
- **Workstream B**: Server compliance (patching, network)

### Expected Output
- Per-user compliance findings
- Per-server compliance findings
- Violation summary with severity

---

## Phase 3: Risk Assessment & Reporting

### Objectives
- Aggregate all findings
- Calculate risk scores
- Generate executive summary
- Recommend remediation priorities

### Risk Scoring
- **Critical**: Immediate action required (score: 10)
- **High**: Action within 7 days (score: 7)
- **Medium**: Action within 30 days (score: 4)
- **Low**: Action within 90 days (score: 2)
- **Info**: No action required (score: 0)

### Report Sections
1. Executive Summary
2. Compliance Score
3. Critical Findings
4. Remediation Roadmap
5. Appendix (detailed findings)

---

## Escalation Criteria

Immediate escalation to CISO required if:
- Any admin account without MFA
- Any system >90 days unpatched
- Any active account with password >180 days old
- Evidence of unauthorized access

## Document History

| Version | Date       | Author | Changes |
|---------|------------|--------|---------|
| 1.0     | 2026-01-15 | eve    | Initial runbook |
| 1.1     | 2026-04-01 | eve    | Updated thresholds |
