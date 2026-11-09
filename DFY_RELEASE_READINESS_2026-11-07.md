# DoForYou Release Readiness — 2026-11-07

**Scope:** staging source audit across `DFY-BE`, `DFY-FE`, and `dfy-mobile`.  
**Decision:** Do not approve production money movement or announce production readiness.

This report supersedes the 2026-10-14 snapshot. It separates verified source/CI evidence from hosted configuration and manual evidence that still require an authorised operator.

## 1. Executive status

- Authentication hardening PRs have already been merged into staging: BE #80 and FE #84.
- The current staging integration work is in open draft PRs. No PR in this audit was merged, and no Render deployment or production setting was changed.
- Backend CI has passed for PR #82 and PR #84. PR #83's latest run is still testing after its webhook-signature changes. PR #81's workflow is waiting for action before jobs start.
- FE PR #86 CI passed. Mobile PR #26 CI passed and uploaded an Android APK artifact, but the rolling staging-branch GitHub Release has not been published because the PR is not merged.
- Production remains a separate branch/environment and is not approved for live payments or transfers.

## 2. Branch separation

Latest branch comparison observed during this audit:

| Repository | Staging vs main | Release implication |
|---|---:|---|
| `DFY-BE` | 255 commits ahead, 0 behind | Do not merge the entire staging branch into main. |
| `DFY-FE` | 4 commits ahead, 1 behind | Keep staging changes isolated; reconcile deliberately before any production release. |
| `dfy-mobile` | 2 commits ahead, 0 behind | Keep the staging APK workflow separate from the main release. |

These counts are point-in-time observations and must be refreshed before any branch promotion.

## 3. Open staging pull requests

| Repository / PR | Scope | Latest verification |
|---|---|---|
| [DFY-BE #81](https://github.com/DoForYou-ZA/DFY-BE/pull/81) | Retire unsafe legacy Ozow payout callbacks and correct Paystack provider defaults | Draft; workflow is `action_required`. Hold until live staging configuration and historical callback dependencies are inspected. |
| [DFY-BE #82](https://github.com/DoForYou-ZA/DFY-BE/pull/82) | Guard payout/refund races, handle `refund.needs-attention`, resolve refund webhooks by payment reference, and add a guarded staging Test Mode transfer opt-in | Latest backend CI run #520 passed. |
| [DFY-BE #83](https://github.com/DoForYou-ZA/DFY-BE/pull/83) | Verify timestamped `Paystack-Signature`, retain safe legacy fallback, and verify transaction identity/currency | Latest backend CI run #521 passed, including build, EF snapshot verification and tests. |
| [DFY-BE #84](https://github.com/DoForYou-ZA/DFY-BE/pull/84) | Allow escrow release and payout queueing before runner bank setup is complete | Latest backend CI run #507 passed, including EF model snapshot verification and tests. |
| [DFY-BE #85](https://github.com/DoForYou-ZA/DFY-BE/pull/85) | Refresh this release-readiness report and README link | Documentation-only; CI was not triggered for the documentation-only changes. |
| [DFY-FE #86](https://github.com/DoForYou-ZA/DFY-FE/pull/86) | Make the staging Docker build use the current staging-branch APK release | Latest frontend CI run #709 passed. Depends on the mobile staging release being published first. |
| [dfy-mobile #26](https://github.com/DoForYou-ZA/dfy-mobile/pull/26) | Publish a rolling staging-branch APK and pass Google OAuth client IDs into the build | Latest mobile CI run #287 passed; Android artifact uploaded. Release publication is intentionally skipped on a pull-request run. |

All seven PRs are draft, including this documentation update. CI success does not authorise merging or deploying. Coordinate merge order: BE #83 before BE #82 (the latter also changes the payment webhook controller); BE #82 before BE #84 (both touch the escrow-release notification); mobile #26 before FE #86. Keep BE #81 on hold until its Render configuration check is complete.

## 4. Verified backend financial rules and findings

### Canonical task and payout lifecycle

1. Creator creates a task and completes Paystack Checkout.
2. The backend independently verifies the Paystack transaction before activating the paid task.
3. Runner accepts and completes the task.
4. Task enters `AwaitingConfirmation`; creator has 24 hours to confirm or dispute.
5. A DFY task dispute blocks normal payout release.
6. If no confirmation or dispute is recorded by expiry, the backend queues a runner payout.
7. A runner is marked paid only after Paystack confirms transfer success.

The application states `EscrowHeld` and `EscrowReleased` describe DFY's workflow. They do not prove regulated escrow, safeguarding or a trust-account arrangement.

### Commission

The source calculates 15% commission with a R5 minimum. For a R200 task, the intended allocation is R30 commission and R170 runner earnings before provider fees. The backend is the source of truth; clients must display persisted payout values rather than recomputing them.

### Bank setup and escrow release

The current staging source blocks escrow release when the runner has no active verified Paystack bank account. That conflicts with the intended rule that bank setup is not an acceptance/completion prerequisite and payout readiness is separate from task completion.

BE #84 changes the payout relationship to allow an unassigned payout, queues it as `AwaitingBankDetails`, and attaches/requeues it after a bank account is verified. Until that PR is merged and deployed, the existing restriction remains in staging.

### Payout processing

- Automatic Paystack transfers remain explicitly opt-in through `PAYSTACK_AUTOMATIC_PAYOUTS_ENABLED`.
- `DFY_E2E_TEST_MODE=true` normally prevents the payout worker from submitting or reconciling provider transfers.
- BE #82 adds a separate override that only permits Test Mode transfers when the environment is Staging, `DFY_PAYSTACK_TEST_TRANSFERS_ENABLED=true`, and `PAYSTACK_SECRET_KEY` begins with `sk_test_`. The automatic payout opt-in must also be true to submit queued transfers.
- The override is code only; no Render environment flags were changed.
- Existing retry paths cover specific recoverable conditions such as insufficient Paystack balance and missing recipient setup. A general audited operator retry workflow for every terminal transfer failure was not confirmed in the inspected source. Do not retry an ambiguous transfer without provider reconciliation and reference-level idempotency.
- A queued or processing payout is not a successful bank payout. Do not promise instant payout.

### Refunds

- BE #82 adds handling for Paystack `refund.needs-attention` and refund events that identify the original payment through `transaction_reference` without a refund ID.
- A needs-attention refund remains on refund hold; stale `refund.pending`/`refund.processing` events must not downgrade it.
- The backend does not yet provide a complete in-app workflow to collect and submit additional customer bank details required by Paystack. An authorised operator must resolve those cases through the approved provider operations process.
- Full/partial refund policy and customer cancellation policy still require product/legal approval.

### Webhook signing

- The current integration historically used legacy `x-paystack-signature` HMAC-SHA512 with the Paystack API secret.
- BE #83 adds the timestamped `Paystack-Signature` header using a dedicated `whsec_` webhook signing secret, a 300-second timestamp window, multiple `v1` signatures for rotation and constant-time comparison.
- The new header is preferred whenever present. Invalid new signatures must not fall back to the legacy header.
- Hosted configuration still needs an authorised operator to generate and set `PAYSTACK_WEBHOOK_SECRET` (or `PAYSTACK_WEBHOOK_SECRETS` during rotation). Keep the API secret available while Paystack still sends the legacy header.

### Settlement and split payments

- Read-only admin reconciliation endpoints exist for DFY ledger consistency, Paystack settlement batches/transactions, and payment operations.
- Settlement reconciliation is not proof that the merchant bank account received the credit; successful provider settlement records must be compared with the actual bank statement.
- The inspected checkout payload does not configure Paystack `subaccount` or `split_code`; automatic commission split settlement is not implemented.
- Paystack documents split payments/subaccounts, but that does not by itself prove that a particular South African merchant account is approved for DFY's delayed runner payout/dispute model. Obtain written provider confirmation and legal advice before adopting it.
- Chargeback events and the full evidence/response workflow are not implemented in DFY. Paystack documents `charge.dispute.create`, `charge.dispute.remind` and `charge.dispute.resolve`; its dispute guidance states that merchants should respond within 16 hours. This is a production blocker until there is an owned operations process, deadline monitoring and evidence workflow.

## 5. Authentication and identity configuration

Source contains backend Google ID-token validation, TOTP authenticator support, Sumsub identity verification, and Brevo email delivery integration. The hosted configuration has not been verified in this report.

Required configuration to inspect:

| Capability | Configuration to verify |
|---|---|
| Website Google sign-in | FE build variable `VITE_GOOGLE_CLIENT_ID`; BE allowlist `GOOGLE_CLIENT_IDS` |
| Mobile Google sign-in | GitHub Actions variables `EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID`, `EXPO_PUBLIC_GOOGLE_ANDROID_CLIENT_ID`, `EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID`; BE allowlist must include the accepted client IDs |
| Authenticator TOTP | Persistent `DFY_TOTP_ENCRYPTION_KEY` containing a valid 32-byte Base64 key |
| Sumsub identity | `SUMSUB_APP_TOKEN`, `SUMSUB_SECRET_KEY`, `SUMSUB_LEVEL_NAME`, `SUMSUB_WEBHOOK_SECRET` and the correct API base URL |
| Brevo email | `BREVO_API_KEY`, verified `SMTP_FROM`, and confirmed sender-domain DNS authentication |
| Core authentication | `JWT_KEY`, frontend/backend URLs, allowed origins and the intended staging environment settings |

Google sign-in remains unavailable wherever the corresponding client IDs are absent. Mobile PR #26 passes repository variables into the Android build but does not create OAuth client IDs. Apple sign-in, account deletion, report/block, retention and appeal policies, and final consent/legal copy remain product or compliance decisions. Do not claim ID-number encryption unless the implementation and key management are verified.

## 6. Manual validation still required

### Controlled Paystack Test Mode

After verifying the staging secret is a Test Mode key, an authorised operator must deliberately enable the two payout flags for a controlled window:

- `PAYSTACK_AUTOMATIC_PAYOUTS_ENABLED=true`
- `DFY_PAYSTACK_TEST_TRANSFERS_ENABLED=true`
- Confirm `PAYSTACK_SECRET_KEY` begins with `sk_test_` and `ASPNETCORE_ENVIRONMENT=Staging`.
- Keep `DFY_E2E_TEST_MODE=true`; the override is specifically guarded for Staging/Test Mode.
- Run the gated FE manual checkout journey with `PAYSTACK_MANUAL_CHECKOUT=1` and `PAYSTACK_TEST_MODE_CONFIRMED=1`, using designated creator/runner accounts and a Paystack-Test-Mode-verified recipient.
- After the test, turn both payout flags off and verify the worker returns to the safe queued-only E2E behaviour.

Do not enable these settings until the deployed staging secret and account mode have been verified. Never use a live key for this test.

### Native mobile

The Android CI artifact is not proof of a physical-device test. Install the APK and verify registration/login, Google sign-in once configured, browse/search, task creation, creator checkout return, runner acceptance/completion, creator confirmation/dispute, bank setup, payout status and deep links. Confirm the build targets the staging API for staging tests and the production API only in an approved production build.

### Email and identity

Verify actual email delivery (not just a 200 response from the resend endpoint), email verification and reset links, TOTP enrollment/recovery, Sumsub sandbox verification, webhook signature handling and the resulting user eligibility policies.

## 7. Hosted configuration and operator dependencies

The Render workspace was inspected read-only. Both staging services are configured to deploy the `staging` branch from the expected repositories, use Docker in Frankfurt on the Free plan, and have auto-deploy enabled. The latest observed successful deployments were backend commit `f64582c` and frontend commit `97b441c` on 2026-11-07. Render metrics returned no datapoints in the query window, so current CPU, memory, HTTP volume and latency could not be assessed from that result. The Render service details did not expose environment variable values; Google OAuth, Paystack mode, webhook secrets and feature flags remain unverified. No service environment variables, deploy hooks, deployment settings or secrets were changed.

An authorised operator must confirm:

- Which Render workspace owns `dfy-be-staging` and `dfy-fe-staging`.
- Staging Paystack secret mode, payout flags, webhook URL and signing secret.
- Google OAuth IDs, TOTP key, Sumsub credentials and Brevo sender verification.
- `DFY_FE_STAGING_DEPLOY_HOOK` presence in the mobile repository.
- Staging migration/deployment status after the relevant PRs are approved and merged.

Also obtain Paystack's written confirmation of the marketplace/settlement model and South African legal/compliance approval for holding and disbursing customer funds.

## 8. Release decision

**Staging engineering: active, with passing CI for several draft fixes. Hosted configuration: not yet verified. Manual Paystack Test Mode journey: not yet evidenced. Physical-device test: not yet evidenced. Chargeback operations: incomplete. Production: NOT APPROVED.**

Do not merge the full staging branch into main. Do not enable live automatic payouts or production money movement until all P0 provider, reconciliation, dispute, compliance, configuration and device evidence is reviewed by the release owner.
