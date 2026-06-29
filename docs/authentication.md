# Authentication

Freeboard ships its own authentication: password login, multi-factor (MFA)
with passkeys, TOTP, recovery codes and a magic-link fallback, session
management, sudo-mode step-up for sensitive operations, user administration, and
a one-time first-admin bootstrap. There is no external identity provider; the web
app issues and verifies its own bearer tokens.

This document is for developers and operators. It covers the HTTP API, the
namespace move, the CLI model, and the out-of-band configuration that an operator
MUST supply before the auth feature works.

## At a glance

- All auth API routes live under one prefix: `/api/v1/freeboard`.
- Tokens are opaque bearer tokens. Send them as `Authorization: Bearer <token>`.
- Crypto material (Argon2 secret, token HMAC keys, AES secret-protection keys),
  the WebAuthn relying-party config, the trusted-proxy config, the email sender,
  and the one-time bootstrap secret are REQUIRED operator configuration. They are
  supplied out-of-band (environment variables, user-secrets, or config files) and
  never committed. The app fails loudly at startup if required crypto material is
  missing, empty, or shorter than 32 bytes.

## API namespace move (BREAKING)

All web API routes - the auth endpoints and the existing read endpoints - now
hang off a single prefix:

```text
/api/v1/freeboard
```

The old `/api/*` paths are removed. This is a breaking change for any client that
called the previous paths. There is currently NO 308 redirect shim; an operator
who needs a transition window can add one at the reverse proxy, or a 308
permanent-redirect shim can be added in the app to map old paths to the new
prefix. Prefer fixing clients to call the new prefix.

## Bearer tokens and sessions

A successful login returns an opaque bearer token bound to a server-side session.

- Send `Authorization: Bearer <token>` on authenticated requests.
- Sessions have an absolute lifetime (`Auth:SessionLifetime`, default 24h).
- Logout revokes the current session. Changing or resetting a password revokes
  the user's other sessions.
- A session carries an auth state. A forced-password-reset login yields a LIMITED
  session that can only set a new password until it is upgraded to a full session.

## Auth API

All paths below are relative to the `/api/v1/freeboard` prefix. "Auth" means a
valid bearer token is required; "Admin" means the bearer must be a global admin;
"Sudo" means the session must additionally be in sudo-mode (recent step-up).

### Login and session

| Method | Path | Access | Purpose |
| --- | --- | --- | --- |
| POST | `/auth/login` | public | Password login. Returns a token, or 202 with an `mfa_token` when MFA is required. |
| GET | `/auth/me` | Auth | Return the current authenticated user. |
| POST | `/auth/logout` | Auth | Revoke the current session. |
| GET | `/auth/sessions/{id}` | Auth | Get one session (404 if not owned and caller is not admin). |
| DELETE | `/auth/sessions/{id}` | Auth | Revoke one session. |
| GET | `/users/{id}/sessions` | Auth | List a user's live sessions (admin may list cross-user). |
| DELETE | `/users/{id}/sessions` | Auth | Revoke all of a user's sessions (admin may act cross-user). |

### Password lifecycle

| Method | Path | Access | Purpose |
| --- | --- | --- | --- |
| POST | `/auth/password/change` | Auth | Change password with old-password proof; revokes other sessions. |
| POST | `/auth/password/forgot` | public | Always returns 200. Emails a reset token when reset is enabled and a sender is registered. |
| POST | `/auth/password/reset` | public | Reset the password via a reset token; revokes all sessions. |
| POST | `/account/password` | Auth | Set the password on a forced-reset LIMITED session and upgrade it to full. |

The forgot-password flow returns a uniform 200 regardless of whether the account
exists, so it leaks nothing. It requires `Auth:PasswordResetEnabled = true` and a
registered email sender; with reset enabled but no sender, the app fails fast at
startup.

### MFA two-step login

When a user has MFA factors, `/auth/login` returns 202 with a short-lived
`mfa_token`. The client completes the second step against one of these, supplying
the `mfa_token`. They are public (the `mfa_token` is the credential) and
rate-limited.

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/auth/mfa/totp` | Verify a TOTP code. |
| POST | `/auth/mfa/passkey/options` | Get WebAuthn assertion options for the challenge. |
| POST | `/auth/mfa/passkey` | Verify a passkey assertion. |
| POST | `/auth/mfa/recovery` | Consume a one-time recovery code. |
| POST | `/auth/mfa/magic-link/send` | Email a single-use, short-TTL magic-link token (fallback). |
| POST | `/auth/mfa/magic-link/verify` | Verify a magic-link token. |

Magic-link is a fallback for users who have no passkey or TOTP. It is only
offered when an email sender is registered.

### MFA enrollment (management)

All enrollment endpoints require Auth AND sudo-mode (a recent step-up). Enrolling
the first strong factor returns a one-time recovery-code set.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/auth/mfa` | List active factors and remaining recovery codes (Auth only). |
| POST | `/auth/mfa/totp/enroll` | Begin TOTP enrollment; returns a provisioning URI. |
| POST | `/auth/mfa/totp/activate` | Confirm the TOTP code and activate it. |
| DELETE | `/auth/mfa/totp` | Remove TOTP. |
| POST | `/auth/mfa/passkey/register-options` | Get WebAuthn registration options. |
| POST | `/auth/mfa/passkey/register` | Complete passkey registration. |
| DELETE | `/auth/mfa/passkey/{id}` | Remove a passkey (404 if not owned). |
| POST | `/auth/mfa/recovery/regenerate` | Generate a fresh recovery-code set. |

### Sudo-mode (step-up)

Sensitive operations (MFA enrollment) require sudo-mode: a recent re-confirmation
of a factor. `POST /auth/sudo` re-confirms any currently usable factor (the same
set login accepts) - or the password for a non-MFA user - and stamps the session
so the sudo window opens (`Auth:SudoModeTtl`, default 5 minutes).

| Method | Path | Access | Purpose |
| --- | --- | --- | --- |
| POST | `/auth/sudo` | Auth | Re-confirm a factor (or password) to enter sudo-mode. |
| POST | `/auth/sudo/passkey/options` | Auth | Get assertion options for a passkey sudo step. |
| POST | `/auth/sudo/magic-link/send` | Auth | Email a sudo magic-link (only for magic-link-only users). |

The sudo magic-link send is find-or-create-and-increment in one atomic store
call: at most one active sudo magic-link challenge exists per user, so the
per-challenge re-send cap holds even under concurrent first sends.

### User administration (admin only)

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/users` | Create a user; returns a one-time temporary password. |
| GET | `/users` | List users. |
| GET | `/users/{id}` | Get a user by id. |
| POST | `/users/{id}/disable` | Disable a user and revoke their sessions. |
| POST | `/users/{id}/enable` | Enable a user. |
| POST | `/users/{id}/reset-password` | Force a reset; returns a one-time temporary password. |

### First-admin bootstrap

| Method | Path | Access | Purpose |
| --- | --- | --- | --- |
| POST | `/setup` | secret | Create the first admin. Returns the admin user and a token once. |

`/setup` takes the one-time bootstrap secret in the `X-Freeboard-Bootstrap-Secret`
header or a `bootstrap_secret` body field, compared in constant time. It is
rate-limited per IP, returns 409 once an admin exists, and is disabled when the
secret is unset.

## CLI

The CLI talks to the API, not the database, for everything except schema
migration:

- The `user` command group (`create`, `list`, `disable`, `enable`,
  `reset-password`, `bootstrap`) calls the HTTP API only. It never touches the
  database. Base URL comes from `--api-url` or `FREEBOARD_API_URL`; the admin
  bearer from `--token` or `FREEBOARD_ADMIN_TOKEN`.
- `user bootstrap` posts to `/setup` to create the first admin and prints the
  returned token once. The bootstrap secret comes from `--bootstrap-secret` or
  `FREEBOARD_BOOTSTRAP_SECRET`.
- Only `system migrate` touches the database directly, applying the schema. Its
  connection string comes from `--connection-string` or `FREEBOARD_DB`.

This keeps the community CLI free of any direct user-store or crypto dependency:
user administration is an API client, and the only DB-touching command is the
migration runner.

## Required out-of-band configuration

These values are REQUIRED operator configuration. Supply them via environment
variables, .NET user-secrets, or config files. NEVER commit them. The web app
validates the crypto material at startup and fails loudly if any set is missing,
empty, or has an entry shorter than 32 bytes.

Each crypto key set is a versioned map (`<version> -> base64-key`) with a sibling
"current version" int, so keys can be rotated while old hashes/tokens/ciphertexts
keep verifying under the version recorded with them. The config keys live under
the `Auth` section.

### Argon2 keyed secret (password pepper)

- `Auth:PasswordSecrets:<version>` - base64 raw secret bytes (>= 32 bytes).
- `Auth:CurrentPasswordSecretVersion` - the version new hashes use.

Mixed in as Argon2's native keyed secret, not by concatenation. Verify selects
the secret by the version recorded in the stored PHC string.

### Token HMAC key set

- `Auth:TokenKeys:<version>` - base64 raw key bytes (>= 32 bytes).
- `Auth:CurrentTokenKeyVersion` - the key id new tokens use.

All server-issued or server-verified tokens (sessions, password-reset,
MFA-challenge, recovery codes, magic-link) are stored as keyed HMACs, never bare
hashes. As a deployment shorthand the token key set is also referred to as
`FREEBOARD_AUTH_TOKEN_KEYS`; map it to the `Auth:TokenKeys` versioned entries
(in .NET, the environment form is `Auth__TokenKeys__<version>`).

### TOTP / secret-protection AES key set

- `Auth:SecretProtectionKeys:<version>` - base64 raw 32-byte AES-256 key.
- `Auth:CurrentSecretProtectionKeyVersion` - the version new ciphertexts use.

Used to seal at-rest secrets whose plaintext must be recoverable (TOTP secrets)
with AES-256-GCM. Unseal selects the key by the version stored with the
ciphertext.

### WebAuthn relying party

- `Auth:WebAuthn:RpId` - the relying-party id (registrable domain), e.g.
  `freeboard.example`.
- `Auth:WebAuthn:RpName` - a human-readable name shown by authenticators.
- `Auth:WebAuthn:Origins` - the allowed full origins, e.g.
  `https://freeboard.example`.

`RpId` and `Origins` are REQUIRED outside Development; the app fails at startup if
they are unset there, so a deployment cannot silently accept any origin.

### Trusted proxy / forwarded headers

Configure these only when running behind a reverse proxy:

- `Auth:ForwardedHeaders:KnownProxies` - trusted proxy IPs.
- `Auth:ForwardedHeaders:KnownNetworks` - trusted proxy CIDR networks.

When at least one is configured the app honours `X-Forwarded-For`,
`X-Forwarded-Proto`, and `X-Forwarded-Host`. When neither is configured those
headers are ignored and the socket peer address is used, so a client cannot spoof
its IP to evade rate limiting.

### Email transport

Email delivery goes through a generic `IEmailSender` seam (in `Freeboard.Core`).
The web layer's `AuthEmailService` builds the magic-link and password-reset
messages and hands them to the configured sender. The transport is selected by
`Email:Transport`:

- `none` (default) - no sender is registered, so `AuthEmailService` is not
  registered. With password reset enabled and no transport, the app fails fast at
  startup. Without a sender, magic-link is simply not offered.
- `log` - a non-delivering developer sink. It logs the subject and recipient
  only, never the body (which carries the token), so a registered sender exists
  (password reset can be enabled, magic-link is offered) but no email is
  delivered. It is for wiring and local development, not a working transport, and
  the app logs a startup warning when it is selected. For a real, clickable link
  in development, run the `smtp` transport against a local Mailpit.
- `smtp` - a real SMTP transport (MailKit).

`Email` settings (generic, transport-level):

- `Transport` - `none` | `log` | `smtp`.
- `FromAddress`, `FromName` - the From identity on every email.
- `Smtp:Host`, `Smtp:Port` (default 587).
- `Smtp:UseStartTls` (default `true`) - STARTTLS so tokens are never sent in the
  clear. An operator must explicitly set it `false` to send over an unencrypted
  connection (e.g. a local Mailpit on 1025).
- `Smtp:Username` - when empty, the sender skips authentication.
- `Smtp:Password` - a secret. Supply it via env / user-secrets / a config
  provider and never commit it.
- `Smtp:TimeoutSeconds` (default 30) - bounds a hung connect/send.

The auth-link base URL is auth-specific and lives under auth config, not the
generic `Email` section:

- `Auth:Email:BaseUrl` - the absolute http(s) base the reset / magic-link URLs
  are built from. When an email transport is configured, an invalid or missing
  value fails fast at startup.

When `Transport=smtp` the app fails fast at startup unless `Email:Smtp:Host` and
a parseable `Email:FromAddress` are set.

The SMTP delivery integration test is gated on `FREEBOARD_TEST_SMTP` and runs
against a local Mailpit; see the Testing section of `CLAUDE.md`.

### One-time bootstrap secret

- `Auth:BootstrapSecret` (also `FREEBOARD_BOOTSTRAP_SECRET`) - the one-time
  first-admin bootstrap secret.

A wrong or absent secret rejects `/setup` with 401. An empty value disables
setup. Unset it after the first admin exists.
