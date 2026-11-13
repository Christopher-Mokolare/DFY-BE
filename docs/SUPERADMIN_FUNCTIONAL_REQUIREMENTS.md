# DFY SuperAdmin Functional Requirements and Delivery Status

**Scope:** DFY-BE is the authorization and business-rule source of truth; DFY-FE is the administrative interface. All implementation work targets `staging`. Production must not be changed as part of this work.

## Role model

- `UserType` is marketplace participation: Creator, Runner, or Both.
- `Roles` is system authorization: User, Admin, SuperAdmin.
- A marketplace role must never grant administrative access.
- Backend policies and persisted account state are authoritative. Frontend route guards are presentation only.

## Delivery status at the start of this work

| Domain | Existing capability | Required SuperAdmin objective | Status |
|---|---|---|---|
| Admin accounts | SuperAdmin-only role changes with a reason, fresh authenticator verification, session-stamp rotation; first SuperAdmin staging bootstrap; 48-hour single-use email invitations with hashed tokens, verified-email matching, recipient MFA, revoke/list, and audit events; two-person SuperAdmin grant/revoke requests with distinct requestor, target and approver, reasoned decisions, expiry and last-admin protection; persisted MFA enrollment policy on Admin endpoints | Dedicated admin directory; secure invitations; controlled promotion/revocation; MFA; session revocation; two-person SuperAdmin changes | Partial; invitation and two-person approval APIs/UI and backend Admin MFA policy implemented on feature branch, pending full CI and staging verification |
| User lifecycle | Search/list (admin API), verify, role update, archive/restore, task history | Full lifecycle controls, duplicate-account resolution, account corrections, session revocation, security history | Partial |
| KYC | Manual review queue, reviewer claims, document retrieval, decisions, review SLA and retention services | Permission-separated document access, access logging, override/reopen, SLA reporting and controlled retention | Partial; document-access audit requires verification |
| Tasks | List, verify/unverify, bulk verify, cancel/soft-delete, force-release escrow API | Full task lifecycle history, safe edits, stuck-task interventions and incident linkage | Partial |
| Disputes | List and resolve | Full case lifecycle, evidence review, reopen and resolution history | Partial |
| Payments | Payment overview, read-only reconciliation, Paystack settlement/transfer inspection, bank-account verification, refunds reconciliation; forced escrow release and financial dispute resolution restricted to SuperAdmin with fresh MFA | Audited financial operations, controlled refunds/adjustments, dual approval and payout controls | Partial; forced release/dispute actions now SuperAdmin-only, but independent dual approval for exceptional money movement/refunds remains required |
| Categories | Admin UI and backend CRUD/reorder; required reason; duplicate-name validation; category rename updates task references; deletion blocked while tasks reference the category; audit events committed atomically | Complete CRUD with audit and validation | Implemented on feature branches; pending CI and staging verification |
| Content | Testimonial CRUD/publish/reorder | Governed public-content management including FAQ/legal content | Partial |
| Platform configuration | Task limits endpoint and environment-based integration configuration | Audited feature flags, service areas, fee policy, notification templates, maintenance controls and safe configuration UI | Partial |
| Monitoring | Dashboard and notification dead-letter replay | Service health, errors, queue/job visibility, safe retry/cancel and operational reporting | Partial |
| Audit/compliance | Audit-log records and viewer; identity retention service; EF Core rejects audit-row updates/deletes; PostgreSQL triggers block audit UPDATE, DELETE and TRUNCATE | Centralized immutable audit trail, before/after values, sensitive-document access events, POPIA workflows | Partial; migration and DB privilege boundary still need staging verification |
| Impersonation | No complete workflow established | Disabled by default; read-only, visible, reasoned and fully logged if introduced | Not implemented |
| Tenant/subscription controls | DFY is currently a marketplace, not a multi-tenant subscription SaaS | Not a launch requirement | Out of scope |

## Work delivered in this branch

1. Added a persisted-role-checked SuperAdmin account directory API. It returns paginated account metadata and does not expose password hashes, authenticator secrets, recovery codes, identity numbers, or document contents.
2. Added a SuperAdmin-only Admin Management route and page in DFY-FE.
3. The page supports promoting an existing email-verified account to Admin and revoking Admin access. The person must first register using their own account and verify their email; this is not an invitation workflow.
4. Existing backend role mutation protections remain authoritative: actor must be a persisted SuperAdmin, must not be changing their own role, must have authenticator MFA enabled and recently verified, must supply a reason, and cannot modify a SuperAdmin through the one-person path.
5. Strengthened Admin provisioning so archived or email-unverified accounts cannot receive Admin access.
6. Added backend regression tests for directory authorization, account filtering, and verified-email enforcement.\n7. Added an EF Core append-only guard that rejects updates/deletes to existing audit records, with regression tests.\n8. Added a PostgreSQL migration with triggers rejecting UPDATE, DELETE and TRUNCATE against AuditLogs. This requires staging migration verification and database-owner controls.\n9. Added an Admin invitation workflow: SuperAdmin-only create/list/revoke, 48-hour expiry, SHA-256 token storage, email delivery through the configured sender, recipient email matching, mandatory recent authenticator MFA, one-time acceptance, security-stamp rotation and audit events. The recipient must first register/verify using the invited address.\n10. Added two-person SuperAdmin grant/revoke requests with fresh MFA for both actors, a distinct approver who is neither requester nor target, recorded reasons, 24-hour expiry, security-stamp rotation and final-SuperAdmin protection.\n11. Added an API authorization policy that checks persisted account state, email verification, Admin role and authenticator MFA enrollment on Admin-only endpoints. Existing sensitive workflows continue to require a fresh 15-minute MFA step-up.\n12. Added the task category management page and navigation. Category create/edit/reorder/delete actions require reasons, are audited, and cannot delete a category still referenced by tasks.\n13. Restricted exceptional forced escrow release and financial dispute resolution to persisted SuperAdmins with fresh authenticator verification. This is a stronger role boundary, not a substitute for the still-required two-person financial approval workflow.

## Required remaining work before the SuperAdmin objectives can be called complete

### P0 — privileged access and recovery
- Admin invitation workflow is implemented on the feature branch: hashed single-use token, 48-hour expiry, verified-email matching, recipient MFA, invite/revoke/list, audit events and email delivery. Resend throttling and live staging email delivery still need verification.
- Two-person SuperAdmin grant/revoke workflow is implemented on the feature branch: two distinct SuperAdmins, fresh MFA for both, recorded request/decision reasons, 24-hour expiry and final-SuperAdmin protection. It still needs full test/CI and staging verification.
- Confirm and implement server-side session and refresh-token revocation on role removal, suspension, compromise and password/MFA reset.
- Enforce mandatory MFA enrollment for every Admin and SuperAdmin endpoint through a persisted backend authorization policy; sensitive role, invitation, KYC and SuperAdmin changes additionally require fresh authenticator verification.
- Replace broad role-only checks with granular authorization policies. Keep a compatibility mapping during migration and prove no endpoint is accidentally broadened.

### P1 — marketplace operations
- Complete user lifecycle and account-security controls.
- Complete task and dispute lifecycle management, including reasoned overrides and full history.
- Separate KYC queue access, document viewing, review decisions and override permissions. Audit every document view.
- Keep financial records immutable. Implement refund/adjustment operations only as compensating ledger entries; use dual approval above a configurable threshold. Preserve the manual payout default and existing automatic-payout opt-in.

### P1 — platform and content administration
- Build audited configuration management for task limits, commission policy, categories, service areas, feature flags and notification templates.
- Add controlled FAQ, terms, privacy and public-content management.
- Store secrets write-only: allow replacement/rotation, never return secret values.

### P2 — operations, compliance and resilience
- Add health, error, background-job and notification diagnostics with safe retry/cancel semantics.
- Centralize audit capture for privileged changes, including actor, target, timestamp, source IP, user agent, reason and before/after state.
- Make audit data append-only at the database permission/schema level; no admin endpoint may update or delete audit entries.
- Add POPIA request, retention, export and deletion workflows with legal-hold and financial-record exceptions.
- Keep impersonation disabled unless a separate reviewed design is approved.

## Explicit safety boundaries

- No production branch, Render production service, production database, production secret or live payment configuration is changed by this project.
- No SuperAdmin may view plaintext passwords or retrieve stored API/payment secrets.
- No direct edits or deletes of historical ledger entries or audit records.
- No silent impersonation or unlogged access to identity documents.
- No automatic payout enablement as part of admin-console work.
- High-impact financial actions and destructive bulk operations require separate approval controls.

## Acceptance criteria

The SuperAdmin objectives are complete only when each applicable requirement has:
1. Backend authorization and business-rule enforcement.
2. Frontend workflow with appropriate visibility and confirmation.
3. Automated positive and negative tests, including role-boundary tests.
4. Audit evidence for privileged changes.
5. CI passing on the staging feature branches.
6. Staging deployment and end-to-end verification.
7. No production impact.
