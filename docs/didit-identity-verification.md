# Didit runner identity verification

DFY's backend remains the authority for runner identity status and task/payout eligibility. Didit is an evidence provider only.

## Required provider configuration

Configure these values as Render environment secrets on the **staging backend only**:

- `DIDIT_API_KEY`: Didit API key for the staging/sandbox application.
- `DIDIT_WORKFLOW_ID`: the published workflow ID for the DFY runner workflow.
- `DIDIT_WEBHOOK_SECRET`: the current signing secret for the Didit webhook destination.
- `DIDIT_WEBHOOK_PREVIOUS_SECRET` (optional): the previous signing secret during Didit's 24-hour rotation overlap.
- `IDENTITY_VERIFICATION_CONSENT_VERSION`: the exact approved notice version presented to runners.

Do not put these values in source control, frontend environment variables, mobile app configuration, issue comments, or logs.

The API is considered configured only when API key, workflow ID, webhook secret, and consent version are all present. Missing configuration fails closed.

## Didit workflow requirements

Create and publish a KYC workflow in Didit's staging/sandbox application containing:

1. Government ID verification, enabled for South African documents.
2. Passive liveness.
3. 1:1 face match.

Do not enable AML, DHA/database lookup, NFC or other paid modules unless DFY has explicitly approved the additional cost and purpose. The default full KYC bundle price is not a substitute for confirming the actual modules in the workflow.

Use a distinct workflow/application for production. Do not reuse staging credentials or webhook signing secrets in production.

## Webhook destination

Register a public HTTPS destination pointing to:

`POST https://dfy-be-staging.onrender.com/api/v1/identity-verification/didit/webhook`

Subscribe to `status.updated` and select webhook version `v3`.

Store the destination's `secret_shared_key` as `DIDIT_WEBHOOK_SECRET`.

The receiver:
- Verifies the complete payload with `X-Signature-V2` HMAC-SHA256.
- Checks that `X-Timestamp` matches the signed body timestamp and is within five minutes.
- Supports Didit's `X-Signature-V2-Previous` header during secret rotation.
- Correlates session ID, workflow ID and `vendor_data` to the same DFY runner.
- Deduplicates on Didit's stable `event_id`.
- Uses `created_at` for event ordering, not the retry-refreshed dispatch timestamp.
- Requires top-level `Approved` plus approved ID, liveness and face-match feature results.
- Maps inconclusive or incomplete evidence to `manual_review`; it never approves on missing evidence.

## Client flow

The authenticated runner accepts the current DFY identity notice. DFY issues a short-lived, single-use handoff code. The browser exchanges it with the backend, which creates the Didit hosted session and returns only the hosted verification URL. The browser validates the URL host and redirects to `https://verify.didit.me`.

Mobile opens the same DFY web handoff route. No Didit API key or webhook secret is shipped to either client.

## Verification and release checklist

1. Configure the staging Didit application and workflow.
2. Add the staging Render environment secrets above.
3. Register the staging webhook destination.
4. Use Didit's Try Webhook console with approved, declined and in-review events.
5. Verify valid signatures succeed and invalid/old/replayed signatures fail.
6. Complete real South African Smart ID, green ID book and passport test cases using authorised test participants.
7. Confirm the runner profile refreshes from the backend and that task acceptance remains locked until identity is passed and authenticator 2FA is enabled.
8. Complete DFY's POPIA review, DPA, subprocessor review, cross-border transfer assessment, retention/deletion policy and privacy notice approval.
9. Only after staging is green, create separate production Didit credentials/workflow/webhook and configure production through an approved release.

A sandbox webhook or simulated decision is not evidence of a real person's identity and must never be used to approve a production runner.
