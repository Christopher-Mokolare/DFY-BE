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


## Runner minimum age

Runner activation and runner payout eligibility are restricted to adults aged 18 or older. Ordinary account registration remains available to users aged 16–17. The reviewer must enter the date of birth printed on the submitted identity document when approving a runner application; the backend rejects an approval below age 18 and stores the document-reviewed date of birth on the user record. Runner task and payout capability checks independently enforce the adult-age gate. Do not treat a self-declared date of birth or a valid-looking ID checksum as proof of identity ownership.

## Reviewer ownership and access audit

Pending cases use an exclusive ten-minute reviewer lease. The owner must heartbeat the lease while reviewing; an expired lease may be claimed by another authorised administrator. Document retrieval and decisions require the active claim owner and recent administrator authenticator verification. Audit events record the administrator actor, submission, and viewed document side without copying document content.

## Required staging malware-scanner configuration

Document uploads fail closed unless a private ClamAV daemon is reachable. Configure `DFY_KYC_CLAMAV_HOST` to the private hostname of the staging ClamAV service and `DFY_KYC_CLAMAV_PORT` to its INSTREAM port (normally 3310). Do not expose the daemon publicly. The API performs a ClamAV INSTREAM scan before image normalization/encryption; an infected result is rejected and a scanner outage prevents submission. These settings must be provisioned on staging only and verified with a clean test image and an EICAR test file before enabling runner submissions.
 
## South African ID checksum and duplicate investigation

For Smart ID card and green barcoded ID book approvals, the reviewer enters the 13-digit number read from the document. The backend validates its checksum and encoded adult date of birth, then computes a keyed HMAC using the independent `DFY_KYC_ID_HMAC_KEY` secret. Only the HMAC is stored for duplicate matching; the raw ID number is not persisted by this review flow. A match against another submission, a previously approved fingerprint or a legacy user ID number sets a duplicate-investigation flag and blocks approval without automatically rejecting the runner. Configure a random base64 key of at least 32 bytes on staging only.
