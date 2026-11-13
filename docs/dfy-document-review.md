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

## Review target and reviewer rota

The internal target is one business day, calculated as eight working hours in South African Standard Time:
- Monday–Friday, 09:00–17:00 SAST.
- Weekends and configured South African public holidays excluded.
- Near-deadline alert at 75% of the target.
- Overdue alert to configured primary reviewers and the runner.
- Two-business-day escalation to configured primary and backup reviewers.

Do not advertise the one-business-day target until named primary and backup reviewers have accepted coverage and the staging configuration has been verified.

Configure these values on the staging API service:
- `IdentityVerification__PrimaryReviewerUserIds`: comma-separated internal IDs of active reviewer accounts.
- `IdentityVerification__BackupReviewerUserIds`: comma-separated internal IDs of active backup reviewer accounts.

Only explicitly configured active accounts that still hold the legacy `Admin` role are eligible recipients. An empty or invalid rota fails closed and logs a warning; it does not broadcast to all administrators. The reviewer controller still uses the legacy Admin role guard; permission-based reviewer roles remain part of BE issue #93.

The built-in public-holiday calendar covers 2026 and 2027. Refresh it annually before the next calendar year. Additional dates can be supplied through `IdentityVerification:SouthAfricanPublicHolidays` as comma-separated `yyyy-MM-dd` values. Review the official South African government holiday proclamation each year, record the reviewer who checked the calendar, and add any newly proclaimed dates before the first affected review day.

## Reviewer claims and document access

Pending cases use an exclusive ten-minute reviewer lease. The owner must heartbeat the lease while reviewing; an expired lease may be claimed by another authorised administrator. Document retrieval and decisions require the active claim owner and recent administrator authenticator verification.

Every document view writes an actor-attributed identity-verification event containing the administrator, submission and document side; it does not copy document content. The document is watermarked for review only, returned with no-store headers, and the plaintext buffer is cleared after watermarking when a separate output buffer is created. Never download, email, screenshot, or store identity documents outside the authorised review workflow.

## Notification outbox and operator recovery

Identity submission and review-decision notifications are persisted in the database outbox with idempotency keys. Delivery uses bounded retries and lease-based worker ownership. A notification that exhausts its retry policy moves to the dead-letter state.

Authenticated administrators can view dead-letter metadata at `GET /api/v1/admin/identity-notifications/dead-letters`. Replaying a dead letter additionally requires fresh authenticator verification within 15 minutes and an operational reason of 10–500 characters via `POST /api/v1/admin/identity-notifications/{id}/replay`.

Replay only accepts a row still in the dead-letter state. It requeues the item and writes an audit event with actor, reason, correlation ID, IP and user-agent. The endpoint does not return message bodies or recipient addresses. Check delivery logs and the dead-letter queue during daily review; do not replay delivered or actively processing rows.

## Administrator access

Review endpoints require the Admin role and authenticator verification within 15 minutes. Reviewer claims are exclusive and enforced server-side. Administrators must never request or view passwords, bypass 2FA, or silently change recovery details. Ordinary Admin accounts cannot change other users' roles through the legacy role endpoint; that endpoint now requires an existing SuperAdmin and fresh MFA.

## Staging secrets and key-rotation constraints

Set these only as Render staging secrets/environment variables; never commit secret values:
- `DFY_KYC_DOCUMENT_ENCRYPTION_KEY`: Base64-encoded 32-byte AES-256 key.
- `DFY_KYC_ID_HMAC_KEY`: independent random Base64 key of at least 32 bytes.
- `IDENTITY_VERIFICATION_CONSENT_VERSION` or `IdentityVerification:ConsentVersion`.
- `BREVO_API_KEY` and `SMTP_FROM`, or supported SMTP settings.
- Optional `IdentityVerification:SouthAfricanPublicHolidays`.

**Do not rotate either identity key by simply replacing its environment variable.** The current document encryption implementation uses one configured AES key and does not persist a key-version identifier with each ciphertext. Replacing it would make existing documents undecryptable. The duplicate-ID HMAC implementation likewise does not persist a key version or dual fingerprints; replacing its key would break matching against previously stored fingerprints.

A safe rotation requires a separately reviewed dual-key/key-version implementation, controlled re-encryption or fingerprint backfill, backup verification, and a rollback plan. Until that implementation exists, keep the current keys in a managed secret store with tightly restricted access, encrypted backup, documented custody and periodic access review. Do not describe automatic KMS rotation as supported by the current code.

## Retention and malware scanning

Documents are encrypted at rest and scheduled for deletion 90 days after a decision. Unreviewed submissions use the fallback retention deadline. Verify the purge process in staging before sign-off.

Document uploads fail closed unless a private ClamAV daemon is reachable. Configure `DFY_KYC_CLAMAV_HOST` to the private hostname of the staging ClamAV service and `DFY_KYC_CLAMAV_PORT` to its INSTREAM port (normally 3310). Do not expose the daemon publicly. The API performs a ClamAV INSTREAM scan before image normalization/encryption; an infected result is rejected and a scanner outage prevents submission. Verify with a clean test image and an EICAR test file before enabling runner submissions.

## South African ID checksum and duplicate investigation

For Smart ID card and green barcoded ID book approvals, the reviewer enters the 13-digit number read from the document. The backend validates its checksum and encoded adult date of birth, then computes a keyed HMAC using the independent `DFY_KYC_ID_HMAC_KEY` secret. Only the HMAC is stored for duplicate matching; the raw ID number is not persisted by this review flow. A match against another submission, a previously approved fingerprint or a legacy user ID number sets a duplicate-investigation flag and blocks approval without automatically rejecting the runner.

## Runner minimum age

Runner activation and runner payout eligibility are restricted to adults aged 18 or older. Ordinary account registration remains available to users aged 16–17. The reviewer must enter the date of birth printed on the submitted identity document when approving a runner application; the backend rejects an approval below age 18 and stores the document-reviewed date of birth on the user record. Runner task and payout capability checks independently enforce the adult-age gate. Do not treat a self-declared date of birth or a valid-looking ID checksum as proof of identity ownership.

## Staging sign-off checklist

- [ ] At least one named primary and one named backup reviewer are assigned and their user IDs are configured on staging.
- [ ] Reviewer claims, heartbeat, expiry and cross-reviewer exclusion are tested.
- [ ] Upload, request-information, resubmission, approval and rejection are exercised end-to-end.
- [ ] A clean document succeeds; EICAR and scanner-unavailable tests fail closed.
- [ ] Document-view and decision events identify the actor and contain no document payload.
- [ ] Outbox retries, dead-letter visibility and reasoned replay are exercised.
- [ ] Retention purge is verified without deleting unresolved cases.
- [ ] Public-holiday dates for the next calendar year are reviewed and recorded.
- [ ] Encryption/HMAC key custody is documented; no key replacement is attempted until versioned rotation is implemented.

## Release policy

Staging only. Do not merge to main, alter production data, deploy production, or enable production identity checks without explicit approval. Account corrections remain a separate dual-approval workflow under BE issue #90. Full persisted permission-based RBAC and role-scoped reviewer permissions remain under BE issue #93.
