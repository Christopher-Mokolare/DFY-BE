# DFY Document Review — Staging Operations

## Purpose and limits
DFY Document Review is a first-party manual review of identity-document images. The technical method is `manual_document_review`. Approval means an authorised administrator reviewed the images; it does not establish a Home Affairs/database match, biometric match, liveness result, or conclusive proof that the applicant is the rightful holder. No external identity-verification provider is called.

## Accepted runner documents
- South African Smart ID card — front and back required.
- South African green barcoded ID book — photo-information/details page.
- South African passport — photo-information page.

JPG/PNG only, maximum 5 MB per image. Images must be clear and uncropped. No selfie is requested. Driver's licences and residence/refugee permits are not accepted in this launch workflow.

## Decisions
Backend-validated reason codes for reject/request-information:
`document_unclear`, `back_side_missing`, `document_expired`, `name_mismatch`, `id_number_unreadable`, `suspected_tampering`, `other`.

The administrator supplies a runner-facing explanation. A separate `InternalReviewNote` is private and is not returned by the runner status endpoint. User-facing approval wording is **DFY Identity Document Reviewed**, not “identity verified”.

## Review target
The target is one business day, calculated as eight working hours in South African Standard Time:
- Monday–Friday, 09:00–17:00 SAST.
- Weekends and configured South African public holidays excluded.
- Alert administrators at 75% of the target.
- Alert administrators and notify the runner on breach.
- Escalate to all administrator accounts at the two-business-day internal maximum.

Only advertise this target once DFY has staffed primary and backup reviewer coverage. Reviewer claim/heartbeat and a configured backup rota remain open in DFY-BE issue #91.

The built-in public-holiday calendar covers 2026 and 2027 and must be refreshed annually. Additional dates can be supplied through `IdentityVerification:SouthAfricanPublicHolidays` as comma-separated `yyyy-MM-dd` values.

## Administrator access
Review endpoints require the Admin role and authenticator verification within 15 minutes. Administrators must never request or view passwords, bypass 2FA, or silently change recovery details. Document responses use no-store headers. Full actor attribution, watermarking and reviewer claims remain open in issue #91.

## Staging configuration
Set these only as Render staging secrets/environment variables; never commit secret values:
- `DFY_KYC_DOCUMENT_ENCRYPTION_KEY`: Base64-encoded 32-byte AES-256 key.
- `IDENTITY_VERIFICATION_CONSENT_VERSION` or `IdentityVerification:ConsentVersion`.
- `BREVO_API_KEY` and `SMTP_FROM`, or supported SMTP settings.
- Optional `IdentityVerification:SouthAfricanPublicHolidays`.

Key rotation must preserve decryptability of existing ciphertext; managed KMS/Secrets Manager rotation remains open in issue #91.

## Retention
Documents are encrypted at rest and scheduled for deletion 90 days after a decision. Unreviewed submissions use the fallback retention deadline. Verify the purge process in staging before sign-off.

## Remaining sign-off gaps
- Durable email retry/outbox and dead-letter monitoring.
- Reviewer claim, heartbeat and release.
- Full document-view actor attribution, watermarking, malware scanning, EXIF removal, ID checksum validation, keyed duplicate-ID investigation flags, and full concurrency/retention E2E tests (issue #91).
- Controlled account corrections are separate from document review (issue #90); no unrestricted super-admin editing.
- Runner minimum-age policy remains a product/legal decision and must be confirmed before backend enforcement.

## Release policy
Staging only. Do not merge draft PRs, alter production data, deploy production, or enable production identity checks without explicit approval.
