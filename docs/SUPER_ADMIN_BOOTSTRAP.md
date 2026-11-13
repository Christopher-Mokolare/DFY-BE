# First Super Admin bootstrap (staging only)

The first Super Admin is provisioned through the normal verified account and authenticator enrollment flow. Registration with the bootstrap email does not grant any administrative access.

## Preconditions

1. Register or locate the existing `info@doforyou.co.za` account on the staging site.
2. Complete its normal email-verification link.
3. Sign in and enroll authenticator MFA in Account > Security.
4. Confirm the authenticator status reports enabled.
5. Generate a one-time bootstrap secret outside ChatGPT and store it temporarily in a password manager, for example with `openssl rand -base64 48`.

## Staging-only configuration

Temporarily set these variables on the **dfy-be-staging** Render service only:

- `DFY_SUPERADMIN_BOOTSTRAP_ENABLED=true`
- `DFY_SUPERADMIN_BOOTSTRAP_SECRET=<generated high-entropy secret>`

Never commit the secret, put it in a URL, paste it into a support ticket, or write it to logs. The endpoint returns 404 outside ASP.NET Staging, and it returns 404 when either setting is absent.

## Execute once

While signed in as the verified `info@doforyou.co.za` account, send an authenticated POST to:

`/api/v1/admin/bootstrap/super-admin`

Body:

```json
{
  "bootstrapSecret": "<temporary secret>",
  "authenticatorCode": "<current six-digit authenticator code>"
}
```

The operation checks the account email, email verification, active account status, enrolled MFA, a valid non-replayed TOTP step, and the temporary bootstrap secret. A serializable transaction and immutable audit record make the bootstrap one-time. The account is assigned `Admin,SuperAdmin`, and its security stamp is rotated.

## Mandatory cleanup

Immediately after a successful response:

1. Remove both temporary bootstrap variables from **dfy-be-staging**.
2. Redeploy/restart staging so the API no longer has the bootstrap secret in its process environment.
3. Sign in again because the bootstrap action revoked the current session.
4. Verify the Super Admin account can reach the protected Admin Console.
5. Record the staging audit event and confirm a second bootstrap attempt is rejected.

Do not copy these settings to production. Production bootstrap requires a separate reviewed release procedure and explicit approval. The endpoint is not a substitute for the complete persisted RBAC and role-management work in BE #93.
