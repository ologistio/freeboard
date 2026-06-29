## ADDED Requirements

### Requirement: Opaque session tokens with a key-id prefix, stored as keyed hashes

On a fully authenticated login the system SHALL mint a high-entropy opaque session
secret (32 bytes from a cryptographically secure RNG) and return the raw token to the
client exactly once in the wire format `v<keyId>.<secret-base64url>`. The system SHALL
store only a keyed HMAC-SHA256 of the secret, computed with the HMAC key selected by
the token's key id from a configured versioned key set, together with the stored
`token_key_version`, never the raw token, so a database dump cannot be replayed and
cannot be brute-forced into a lookup without the server key. Tokens SHALL NOT be JWTs
and SHALL carry no client-readable claims. Opaque server-side tokens are chosen for
their own merits: server-side revocability at any moment, no client-readable claims to
leak or tamper with, and no signing-key/alg-confusion attack surface.

#### Scenario: Raw token is never persisted

- **WHEN** a session is created
- **THEN** the database stores only the keyed HMAC of the token plus its key version
  and the raw token exists only in the login response

#### Scenario: Token carries a key id for rotation

- **WHEN** the token key set is rotated to a new current key id
- **THEN** new sessions are minted under the new key id, existing sessions keep
  validating under their stored key version until they expire or are revoked, and
  retiring an old key id invalidates exactly the sessions minted under it

### Requirement: Bearer authentication on protected requests

The web app SHALL authenticate requests carrying `Authorization: Bearer <token>` by
parsing the token key id from the `v<keyId>.` prefix, computing the keyed HMAC of the
presented secret with the selected key, and looking up the session by `token_hash`. It
SHALL then assert the stored `token_key_version` equals the parsed key id as an
integrity check, treating a mismatch as invalid (reject `401`). A request with a
missing, unknown, expired, or revoked token, or whose owning user is disabled, to a
protected endpoint SHALL be rejected with `401`. A token whose key-id prefix is absent
or does not parse, whose key id is not in the configured key set (unknown or retired),
or whose secret is not valid base64url SHALL be rejected with `401` WITHOUT any
database lookup. The `401` SHALL be uniform across all of these conditions and SHALL
NOT reveal which one occurred. A valid token SHALL resolve to its owning user for the
request. An MFA login challenge token SHALL NEVER be accepted as a session bearer on
ANY bearer-protected endpoint.

#### Scenario: Valid token authenticates

- **WHEN** a request to a protected endpoint presents a valid, unexpired,
  unrevoked bearer token for an enabled user
- **THEN** the request is authenticated as the token's owning user

#### Scenario: Expired token rejected

- **WHEN** a request presents a token whose session `expires_at` has passed
- **THEN** the response is `401`

#### Scenario: Disabled user's token rejected

- **WHEN** a request presents a valid token whose owning user has since been disabled
- **THEN** the response is `401`

#### Scenario: MFA challenge token rejected as a bearer

- **WHEN** an MFA login challenge token is presented in the `Authorization: Bearer`
  header of any bearer-protected endpoint
- **THEN** the response is `401` and the request is not authenticated as a session

#### Scenario: Malformed or unknown-key token rejected without a lookup

- **WHEN** a bearer token has a missing/malformed key-id prefix, an unknown or retired
  key id, or a non-base64url secret
- **THEN** the response is a uniform `401`, no database lookup is performed, and the
  response does not reveal which condition failed

### Requirement: Force-password-reset sessions are limited

When a user whose `force_password_reset` is true logs in, the system SHALL issue a
limited session (`auth_state` = limited) that permits ONLY
`GET /api/v1/freeboard/auth/me`, `POST /api/v1/freeboard/auth/logout`, and
`POST /api/v1/freeboard/account/password`. Every other bearer-protected endpoint SHALL
return `403` for a limited session until the required password reset completes. On a
successful `account/password` the SAME session SHALL be upgraded IN PLACE to a full
session; the existing token SHALL remain valid and SHALL NOT change.

#### Scenario: Limited session blocked from normal endpoints

- **WHEN** a force-password-reset user's limited session calls a normal
  bearer-protected endpoint other than me/logout/account-password
- **THEN** the response is `403`

#### Scenario: Same token works after in-place upgrade

- **WHEN** the force-password-reset user completes `account/password`
- **THEN** `force_password_reset` clears, the same token (unchanged) is now a full
  session, and it can call normal endpoints

### Requirement: Get authenticated user

The web app SHALL provide `GET /api/v1/freeboard/auth/me` requiring a valid bearer
session, returning `200` with the authenticated user object (`id` ULID string, `name`,
`email`, `global_role`, `enabled`, `force_password_reset`, `mfa_enabled`, `created_at`,
`updated_at`). It SHALL return `401` without a valid token.

#### Scenario: Me returns the current user

- **WHEN** an authenticated user calls `GET /api/v1/freeboard/auth/me`
- **THEN** the response is `200` with that user's object

### Requirement: Logout revokes the session

The web app SHALL provide `POST /api/v1/freeboard/auth/logout` requiring a valid
bearer session and SHALL revoke (delete) the current session so the token can no
longer authenticate. Logout SHALL return `200`.

#### Scenario: Token unusable after logout

- **WHEN** a user logs out and then reuses the same token
- **THEN** the subsequent request is rejected with `401`

### Requirement: Session management endpoints

The web app SHALL provide session management:
`GET /api/v1/freeboard/auth/sessions/{id}` (session metadata, no token),
`DELETE /api/v1/freeboard/auth/sessions/{id}` (revoke one session),
`GET /api/v1/freeboard/users/{id}/sessions` (list a user's sessions), and
`DELETE /api/v1/freeboard/users/{id}/sessions` (revoke all of a user's sessions). The
`{id}` route params accept the ULID form. A non-admin user SHALL be able to read and
revoke ONLY their own sessions; a non-admin request targeting a session id or a user
the caller does not own SHALL return `404` (not `403`), so existence is not disclosed.
An administrator MAY act cross-user and receives `404` only for ids that do not exist.

#### Scenario: User revokes their own session

- **WHEN** an authenticated user deletes one of their own session ids
- **THEN** that session can no longer authenticate

#### Scenario: Non-admin acting on another user's session gets 404

- **WHEN** a non-admin user calls `GET` or `DELETE` for a session they do not own, or
  `DELETE` on another user's sessions
- **THEN** the response is `404`, no sessions are revoked, and the response does not
  reveal whether the target exists

#### Scenario: Admin may revoke another user's sessions

- **WHEN** an administrator calls `DELETE /api/v1/freeboard/users/{otherId}/sessions`
  for an existing user
- **THEN** that user's sessions are revoked

### Requirement: Sessions expire, carry step-up state, and are revocable in bulk

Each session SHALL have an `expires_at` set from a configurable lifetime with a
documented default of a fixed 24 hours, and SHALL be tied to its user by a foreign key
that cascades on user deletion. The session row SHALL also carry a nullable `sudo_at`
timestamp recording the last successful step-up (sudo-mode) confirmation on that
session (used by the step-up requirement in the `mfa` capability). The system SHALL
support revoking all of a user's sessions, and SHALL revoke other sessions when the
user's password is changed or reset.

#### Scenario: Session expires after the configured lifetime

- **WHEN** a session's `expires_at` (default 24h from creation) has passed
- **THEN** its token no longer authenticates

#### Scenario: Step-up timestamp persists on the session

- **WHEN** a user completes a step-up confirmation on a session
- **THEN** the session's `sudo_at` is set so a later sudo-gated request can check it

#### Scenario: Password change revokes other sessions

- **WHEN** a user changes or resets their password
- **THEN** the user's other existing sessions can no longer authenticate

#### Scenario: Deleting a user removes their sessions

- **WHEN** a user is deleted
- **THEN** that user's session rows are removed by the cascade
