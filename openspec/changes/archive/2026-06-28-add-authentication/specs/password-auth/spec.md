## ADDED Requirements

### Requirement: Email and password login

The web app SHALL provide `POST /api/v1/freeboard/auth/login` accepting
`{ email, password }`. On correct credentials for an enabled user with no MFA factor,
it SHALL return `200` with `{ user, token }` where `user` is the Freeboard user object
(`id` as a ULID string, `name`, `email`, `global_role`, `enabled`,
`force_password_reset`, `mfa_enabled`, `created_at`, `updated_at`) and `token` is an
opaque session token. On incorrect credentials or a disabled account it SHALL return
`401` with a generic message that does not reveal whether the email exists. When the
login rate limit is exceeded it SHALL return `429` with a `retry-after` header and
SHALL NOT compute the password hash. On correct credentials for a user with MFA
enabled it SHALL NOT return a session token; it SHALL return the MFA-required response
defined by the `mfa` capability.

#### Scenario: Successful login without MFA

- **WHEN** an enabled user with no MFA submits a correct email and password within
  the rate limit
- **THEN** the response is `200` with `{ user, token }` and the token authenticates
  subsequent requests

#### Scenario: Wrong credentials do not enumerate users

- **WHEN** a login is submitted with a wrong password, or with an email that does
  not exist
- **THEN** the response is `401` with the same generic message in both cases

#### Scenario: MFA-enabled user is not given a session token at login

- **WHEN** a user with MFA enabled submits correct credentials
- **THEN** the response does not contain a session token and signals that a second
  factor is required

### Requirement: Login does constant work for unknown and disabled accounts

The login path SHALL perform a password verification of comparable cost even when the
account does not exist or is disabled, to avoid a timing oracle that distinguishes
existing from nonexistent or disabled accounts. After the rate-limit checks pass it
SHALL run a dummy Argon2 verification against a fixed decoy hash rather than
short-circuiting. Both the known-account-wrong-password path and the unknown-account
path SHALL invoke the password verifier.

#### Scenario: Unknown account still runs a verification

- **WHEN** a login is attempted for an email that does not exist
- **THEN** the password verifier is still invoked (against a decoy hash) before the
  generic `401` is returned

#### Scenario: Disabled account still runs a verification

- **WHEN** a login is attempted for a disabled account
- **THEN** the password verifier is still invoked before the generic `401` is
  returned

### Requirement: Change password

The web app SHALL provide `POST /api/v1/freeboard/auth/password/change` requiring a
valid bearer session and accepting `{ old_password, new_password }`. It SHALL verify
`old_password` against the stored hash and, on success, store the new password as a
fresh hash. On a wrong `old_password` it SHALL return `422` with a validation-error
body. On success it SHALL revoke the user's other sessions. This endpoint SHALL NOT
require step-up/sudo-mode, because the required `old_password` is itself the
proof-of-presence.

#### Scenario: Change password succeeds

- **WHEN** an authenticated user submits a correct `old_password` and a
  `new_password`
- **THEN** the stored hash is replaced and the user can log in with the new password

#### Scenario: Wrong old password returns 422

- **WHEN** an authenticated user submits an incorrect `old_password`
- **THEN** the response is `422` with a validation-error body and the password is
  unchanged

### Requirement: Forgot and reset password

The web app SHALL provide `POST /api/v1/freeboard/auth/password/forgot` accepting
`{ email }` and SHALL ALWAYS return `200` with an identical status and body regardless
of whether the email exists AND regardless of whether an email sender is configured
(anti-enumeration; no per-request divergence). When the email exists and an email
sender is configured it SHALL issue a single-use, expiring password reset token,
store only the token's keyed hash, and send the raw token via the `IAuthEmailSender`
seam; the raw token SHALL NEVER be returned in a response. If password reset is
enabled with NO email sender configured, the app SHALL fail fast at STARTUP (a
configuration error), so the no-enumeration property always holds at runtime. The web
app SHALL provide `POST /api/v1/freeboard/auth/password/reset` accepting
`{ token, new_password }`; it SHALL reject an invalid, expired, or used token and, on
success, set the new password, mark the token used, and revoke the user's sessions.

#### Scenario: Forgot password is identical for known and unknown emails

- **WHEN** `auth/password/forgot` is called for a known email and for an unknown email
- **THEN** every response has the same `200` status and identical body, revealing
  nothing about account existence

#### Scenario: Missing sender fails at startup, not per request

- **WHEN** the app is configured with password reset enabled and no email sender
- **THEN** the app fails to start with a configuration error rather than diverging on
  a later forgot-password request

#### Scenario: Reset with a valid token

- **WHEN** `auth/password/reset` is called with a valid unexpired token
- **THEN** the password is updated, the token is marked used, and the token cannot
  be reused

#### Scenario: Reset with an invalid token fails

- **WHEN** `auth/password/reset` is called with an unknown, expired, or already-used
  token
- **THEN** the request is rejected and no password change occurs

### Requirement: Force-reset account password

The web app SHALL provide `POST /api/v1/freeboard/account/password` requiring a valid
bearer session (including a force-reset-limited session) and accepting
`{ new_password }`, used when the authenticated user's `force_password_reset` is true.
On success it SHALL store the new password, clear `force_password_reset`, and upgrade
the caller's limited session to a full session. (This replaces an earlier, unwieldy
endpoint name.)

#### Scenario: Force-reset clears the flag and lifts the limit

- **WHEN** a user whose `force_password_reset` is true submits a `new_password` to
  `account/password`
- **THEN** the password is updated, `force_password_reset` becomes false, and the
  caller's session can then reach normal endpoints
