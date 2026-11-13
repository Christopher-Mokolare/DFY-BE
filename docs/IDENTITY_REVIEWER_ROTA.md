# Identity review SLA reviewer rota

The identity review SLA worker sends near-deadline and overdue alerts only to explicitly configured reviewer accounts. It does not broadcast identity-review alerts to every account carrying the legacy Admin role.

Configure these values in the staging API service environment:

- `IdentityVerification__PrimaryReviewerUserIds`: comma-separated internal DFY user IDs for the primary reviewer rota.
- `IdentityVerification__BackupReviewerUserIds`: comma-separated internal DFY user IDs for backup reviewers.

Use only existing, active administrator accounts whose owners are assigned to identity review. The current reviewer controller still uses the legacy Admin authorization claim; do not add an ordinary support or finance administrator to these lists merely to make alerts work.

Near-deadline and one-business-day overdue alerts go to configured primary reviewers. The two-business-day escalation goes to both primary and backup reviewers. The worker verifies that each configured account is active and still has the exact legacy `Admin` role token before queuing an in-app and email notification.

If either list is empty or contains no eligible account, the worker logs a warning and does not fall back to notifying every administrator. Configure and verify at least one primary and one backup reviewer before relying on the one-business-day operational target.

IDs are internal account identifiers, not email addresses. Never place passwords, MFA seeds, document data, or other secrets in these settings. Apply the same configuration separately to staging and production; this change does not modify either environment's settings.
