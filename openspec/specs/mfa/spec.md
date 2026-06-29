# mfa Specification

## Purpose
TBD - created by archiving change add-authentication. Update Purpose after archive.
## Requirements
### Requirement: MFA is optional per user

MFA SHALL be optional per user. A user with no MFA factor SHALL log in directly with
no second step. A user with at least one factor (or the magic-link fallback condition
of the magic-link requirement) SHALL be required to complete a second step after a
correct password. The user's `mfa_enabled` flag SHALL reflect whether a second step is
required.

#### Scenario: User without MFA logs in directly

- **WHEN** an enabled user with no MFA factor submits correct credentials
- **THEN** login returns a session token with no second step

#### Scenario: User with a factor must complete a second step

- **WHEN** a user with an enrolled factor submits correct credentials
- **THEN** login returns an MFA-required response, not a session token

### Requirement: Two-step MFA login challenge with DB-backed challenge tokens

When a user must complete MFA, the system SHALL return `202` with an MFA-required
response containing a short-lived single-purpose MFA challenge token and the list of
available factors, for example
`{ "mfa_required": true, "mfa_token": "...", "factors": ["passkey","totp","recovery"] }`.
The challenge SHALL be persisted as a row whose challenge token is stored only as a
keyed HMAC hash, with the user id, available factors, expiry, a consumed flag, and an
attempt counter, so the challenge survives process restarts, is shared across web
instances, is single-use, expires, and bounds attempts. The `mfa_token` SHALL be
presented in the REQUEST BODY of the MFA-verify endpoints, NEVER in the
`Authorization` header; it SHALL authorize ONLY the MFA verification endpoints and
SHALL NOT be usable as a session bearer. The MFA-verify endpoints SHALL also be
covered by the login rate limiter. On successful completion of any second factor the
system SHALL return `200` with `{ user, token }` - a full session token - and SHALL
mark the challenge consumed. The verify endpoints live under
`/api/v1/freeboard/auth/mfa/`.

#### Scenario: Login signals MFA required

- **WHEN** a user who must complete MFA submits correct credentials
- **THEN** the response is `202` with an MFA challenge token and the available
  factors, a challenge row is persisted with the token stored as a keyed hash, and
  the response contains no session token

#### Scenario: Challenge token rejected as a session bearer

- **WHEN** the MFA challenge token is presented in `Authorization: Bearer` to any
  bearer-protected endpoint
- **THEN** the request is rejected with `401`

#### Scenario: Completing a factor yields a session token

- **WHEN** the user completes any available second factor with a valid, unexpired,
  unconsumed challenge token in the request body
- **THEN** the response is `200` with `{ user, token }` and the challenge is marked
  consumed and cannot be reused

#### Scenario: Expired or consumed challenge rejected

- **WHEN** a second-factor verify is attempted with an expired or already-consumed
  challenge token
- **THEN** the request is rejected and no session token is issued

### Requirement: MFA challenge max attempts

Each MFA challenge SHALL bound failed second-factor attempts. Each failed verify SHALL
atomically increment the challenge `attempts`. After 5 failed attempts the challenge
SHALL be marked consumed (invalidated), and further verifies with that `mfa_token`
SHALL be rejected; the user must restart from login.

#### Scenario: Too many failed attempts invalidates the challenge

- **WHEN** five failed second-factor verifies are made against one challenge
- **THEN** the challenge is consumed and a sixth attempt with the same `mfa_token` is
  rejected, forcing a fresh login

### Requirement: TOTP factor

The system SHALL support TOTP (RFC 6238) as an MFA factor under
`/api/v1/freeboard/auth/mfa/totp`. Enrollment SHALL generate a per-user secret, return
a provisioning URI for an authenticator app, and SHALL activate the factor only after
the user confirms a valid current code. The TOTP secret SHALL be encrypted at rest
(AES-256-GCM) with a key supplied out-of-band (never committed, never in the GitOps
YAML, never stored in the database) and SHALL NOT be stored in plaintext. Verification
SHALL allow a small clock-skew window and SHALL reject reuse of an already-accepted
time step by updating the last accepted step atomically.

#### Scenario: TOTP activates only after a confirming code

- **WHEN** a user enrolls TOTP but does not submit a valid confirming code
- **THEN** the TOTP factor is not active and cannot satisfy a login challenge

#### Scenario: Valid TOTP code completes the challenge

- **WHEN** a user with active TOTP submits a current valid code with a valid MFA
  challenge token
- **THEN** the login completes with a session token

#### Scenario: TOTP secret encrypted at rest

- **WHEN** a TOTP secret is stored
- **THEN** the stored value is ciphertext and the plaintext secret is not recoverable
  from the database alone

#### Scenario: Replayed time step rejected

- **WHEN** a TOTP code for a time step that was already accepted is submitted again
- **THEN** it is rejected because the last accepted step was advanced atomically

### Requirement: Passkey (WebAuthn/FIDO2) factor

The system SHALL support Passkeys (WebAuthn/FIDO2) as an MFA factor under
`/api/v1/freeboard/auth/mfa/passkey`, using a vetted FIDO2 library and requiring user
verification. The Relying Party id and the set of allowed origins SHALL be EXPLICIT
REQUIRED configuration outside local development; the ceremonies SHALL NOT blindly
trust the request `Host`/`Origin` header. Forwarded host/proto headers SHALL be
honored only when the request arrives through a configured trusted proxy. Registration
SHALL run the attestation ceremony for an authenticated user and persist the resulting
credential (credential id, COSE public key, signature counter, user handle, and
metadata), and SHALL reject a registration whose origin or RP-id hash does not match
the configured values. The login second step SHALL run the assertion ceremony, verify
the signature against the stored public key, reject a mismatched origin or RP-id hash,
and store the new signature counter. The signature counter check SHALL NOT reject a
counter of zero (synced passkeys report and keep a zero counter); it SHALL reject a
regression ONLY when both the stored counter and the presented counter are greater
than zero and the presented counter does not exceed the stored counter. In-flight
ceremony state SHALL be correlated to the persisted, single-use challenge row.

#### Scenario: Passkey registration stores a credential

- **WHEN** an authenticated user completes WebAuthn registration
- **THEN** a credential row is stored with its credential id, public key, signature
  counter, and user handle

#### Scenario: Passkey assertion completes the challenge

- **WHEN** a user with a registered passkey completes the assertion ceremony with a
  valid MFA challenge token
- **THEN** the signature is verified against the stored public key, the stored counter
  is updated, and the login completes with a session token

#### Scenario: Synced passkey with zero counter is accepted

- **WHEN** an assertion presents a signature counter of zero from a synced passkey
  whose stored counter is also zero
- **THEN** the assertion is accepted and not treated as a clone

#### Scenario: Counter regression on a stepping authenticator rejected

- **WHEN** an assertion presents a positive counter that does not exceed a positive
  stored counter
- **THEN** the assertion is rejected as a possible clone

#### Scenario: Wrong origin rejected

- **WHEN** a registration or assertion presents an origin or RP-id hash that does not
  match the configured allowed origins / RP id
- **THEN** the ceremony is rejected

### Requirement: Recovery codes

On MFA enrollment the system SHALL generate a set of single-use recovery codes,
display them once, and store only their keyed hashes. A recovery code SHALL ALWAYS be
a valid factor for the MFA login step (it is not gated on other factors being
unavailable) and SHALL be consumed atomically on use. Regenerating recovery codes
SHALL replace the existing set. Because a recovery code is a human-typed string and
cannot carry a key-id prefix, the system SHALL store the key version each code was
signed under and SHALL verify an entered code by HMACing it with the key identified by
that stored key version, so stored recovery codes remain verifiable after the token
HMAC key set is rotated.

#### Scenario: Recovery code completes the challenge once

- **WHEN** a user submits an unused recovery code with a valid MFA challenge token
- **THEN** the login completes and that recovery code can never be used again

#### Scenario: Recovery codes stored only as hashes

- **WHEN** recovery codes are generated
- **THEN** only their keyed hashes are stored and the plaintext codes are shown
  exactly once

#### Scenario: Recovery code verifies after a key rotation

- **WHEN** the token HMAC key set is rotated to a new current key after recovery codes
  were generated under the previous key (still retained)
- **THEN** an unused recovery code still verifies, because verification uses the key
  identified by the code's stored key version, not a prefix parsed from the input

### Requirement: Magic-link fallback factor

The system SHALL offer an emailed magic link as a FALLBACK MFA factor, available ONLY
when the account has `mfa_enabled` true, has no passkey and no TOTP enrolled, AND an
email sender is configured. (Every account has an email - `users.email` is NOT NULL -
so the only runtime gate is the email-sender configuration.) When available,
`magic_link` SHALL appear in the login challenge `factors` list.
`POST /api/v1/freeboard/auth/mfa/magic-link/send` (with the `mfa_token`) SHALL email a
single-use, short-TTL magic-link token stored only as a keyed HMAC on the challenge
row. The magic-link token SHALL be PREFIXLESS (it carries no `v<keyId>.` prefix), like
recovery codes: because it is a separate secret minted later than the challenge token,
the system SHALL store the HMAC key version it was hashed under (in
`magic_link_token_key_version`) and SHALL verify the emailed token by HMACing it with
the key identified by that STORED version (not parsed from the input), so it remains
verifiable after the token HMAC key set is rotated. `send` SHALL be rate-limited and
SHALL cap the number of re-sends per challenge to prevent email-bombing a target.
`POST /api/v1/freeboard/auth/mfa/magic-link/verify` (with the `mfa_token` and the
emailed token) SHALL complete the challenge and return a session token. When no email
sender is configured, `magic_link` SHALL NOT be offered and the send endpoint SHALL
return a clear error.

#### Scenario: Magic link offered as the fallback

- **WHEN** a user with `mfa_enabled` true but no passkey and no TOTP logs in while an
  email sender is configured
- **THEN** the login challenge `factors` list includes `magic_link`

#### Scenario: Magic link verifies after a key rotation

- **WHEN** the token HMAC key set is rotated after a magic-link token was sent under
  the previous (still retained) key
- **THEN** the emailed token still verifies, because verification uses the key
  identified by the stored magic-link key version

#### Scenario: Magic-link send is rate-limited

- **WHEN** magic-link `send` is called repeatedly for one challenge or account
- **THEN** sends are throttled and rejected past the per-challenge cap and the rate
  limit, so a target cannot be email-bombed

#### Scenario: Magic link completes the challenge

- **WHEN** the user requests a magic link and then verifies with the emailed
  single-use token within its TTL
- **THEN** the login completes with a session token and the magic-link token cannot be
  reused

#### Scenario: Magic link not offered without an email sender

- **WHEN** no email sender is configured
- **THEN** `magic_link` is not in the factors list and the send endpoint returns a
  clear error

### Requirement: MFA enrollment and status endpoints

The web app SHALL provide authenticated endpoints under `/api/v1/freeboard/auth/mfa/`
to view MFA status, enroll and activate TOTP, register and remove passkeys, and
regenerate recovery codes. The user's `mfa_enabled` flag SHALL reflect whether at
least one factor is active.

#### Scenario: Enabling a first factor sets mfa_enabled

- **WHEN** a user activates their first MFA factor
- **THEN** the user's `mfa_enabled` becomes true and subsequent logins require the
  second step

#### Scenario: Removing the last factor clears mfa_enabled

- **WHEN** a user removes their only active MFA factor
- **THEN** `mfa_enabled` becomes false and login no longer requires a second step

### Requirement: Sudo-mode (step-up) gates sensitive changes

The system SHALL provide a reusable step-up "sudo-mode" that any endpoint can require.
`POST /api/v1/freeboard/auth/sudo` SHALL re-confirm ANY of the user's currently-usable
factors - the SAME factor set the login MFA challenge would accept for that user
(passkey, TOTP, recovery code, or the magic-link fallback when applicable) - or, for a
user with no MFA, a password re-confirm. On success it SHALL stamp the current
session's `sudo_at` timestamp; it SHALL be rate-limited. An endpoint marked as
requiring sudo-mode SHALL reject a request whose session has no `sudo_at` within the
configured TTL (default 5 minutes) with `403` and a clear "step-up required" body. All
MFA-state mutations (enrolling/activating a factor, removing a factor, regenerating
recovery codes) SHALL require sudo-mode. A valid session token alone, without a recent
step-up, SHALL NOT be sufficient to make a sudo-gated change. Because sudo accepts the
fallback factor, a magic-link-only user SHALL NOT be locked out of enrolling a stronger
factor.

#### Scenario: Sensitive change blocked without step-up

- **WHEN** a request with a valid session but no recent `sudo_at` attempts a
  sudo-gated change (e.g. enroll a factor, remove a factor, regenerate recovery codes)
- **THEN** the response is `403` and no change is made

#### Scenario: Step-up enables the change within the TTL

- **WHEN** the user completes `POST /api/v1/freeboard/auth/sudo` and then performs the
  sudo-gated change within the TTL
- **THEN** the change is allowed

#### Scenario: Fallback-only user enrolls a strong factor after a magic-link sudo

- **WHEN** a magic-link-only user (no passkey, no TOTP) completes
  `POST /api/v1/freeboard/auth/sudo` via the magic-link factor and then enrolls a
  passkey or TOTP within the TTL
- **THEN** the enrollment is allowed, so the user can upgrade to a strong factor

### Requirement: Sudo magic-link sends are independently verifiable

Each accepted sudo magic-link send SHALL store its own single-use, keyed-HMAC
token (with its own key version and expiry) rather than overwriting one shared
token slot on the challenge. A token SHALL verify for as long as it is unconsumed
and unexpired and its challenge is unconsumed and unexpired, so a later send (or a
concurrent send) SHALL NOT invalidate a token that an earlier send already emailed.
The per-challenge re-send cap SHALL be the count of active (unconsumed, unexpired)
tokens for the challenge, enforced atomically so concurrent sends cannot multiply
it. Verifying any one of the challenge's tokens SHALL consume the challenge, which
remains single-use and bound to the user, so at most one token can complete a given
step-up. A freshly created or reset sudo challenge SHALL start with no tokens, so a
token issued for a prior step-up attempt SHALL NOT verify a new one.

#### Scenario: Two sends both verify (no clobbering)

- **WHEN** a user requests a sudo magic-link twice (sequentially or concurrently)
  while under the re-send cap
- **THEN** each send emails a token, and EITHER emailed token verifies the step-up
- **AND** completing the step-up with one token consumes the challenge, so the
  other token can no longer be used

#### Scenario: Re-send cap counts active tokens

- **WHEN** sudo magic-link sends for one challenge reach the configured re-send cap
- **THEN** a further send is rejected without issuing a new token, until an
  existing token is used or expires

#### Scenario: A reset challenge does not honor an old token

- **WHEN** a sudo challenge is reset (its prior instance had expired or been
  consumed) and a new send issues a fresh token
- **THEN** only a token from the new instance verifies; a token from the prior
  instance does not

