# Approved Exemptions Log

This file tracks security exemptions that have been reviewed and approved.

## Active Exemptions

### Service Account: svc-deploy (U008)

| Field | Value |
|-------|-------|
| Exemption Type | MFA Not Required |
| Reason | CI/CD pipeline uses SSH key authentication |
| Approved By | eve (Security Lead) |
| Approval Date | 2026-01-15 |
| Expiration | 2026-06-30 |
| Review Notes | Key rotation enforced every 90 days |

### Service Account: svc-backup (U009)

| Field | Value |
|-------|-------|
| Exemption Type | MFA Not Required |
| Reason | Backup automation uses certificate-based auth |
| Approved By | eve (Security Lead) |
| Approval Date | 2026-01-15 |
| Expiration | 2026-06-30 |
| Review Notes | Certificate renewed annually |

## Expired/Revoked Exemptions

### Test Account: test-admin (U010)

| Field | Value |
|-------|-------|
| Exemption Type | MFA Exemption (EXPIRED) |
| Reason | Legacy admin account for migration testing |
| Approved By | previous-security-lead |
| Expiration | 2025-12-31 |
| Status | **EXPIRED - REVOKE ACCESS** |
| Action Required | Disable account immediately |

---

*Last updated: 2026-04-15*
