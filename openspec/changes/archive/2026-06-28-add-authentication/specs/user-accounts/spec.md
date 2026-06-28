## ADDED Requirements

### Requirement: MySQL-backed user store

The system SHALL persist user accounts in MySQL in the existing MIT
`Freeboard.Persistence` project, reusing `IDbConnectionFactory` and the embedded
migration mechanism. Each user SHALL have a server-generated immutable `id` (a ULID
stored as Crockford base32 `CHAR(26)` with binary collation), an `email` (display)
and an `email_normalized` (lookup key), a `name`, a `global_role`, an `enabled` flag,
a `force_password_reset` flag, an `mfa_enabled` flag, and `created_at`/`updated_at`.
Password material SHALL be stored in a separate `user_password_credentials` table
keyed by user id, NOT on the user profile row, so profile reads never carry the hash.
No user credential material SHALL be stored in `Freeboard.Core`, and no database or
socket dependency SHALL be added to `Freeboard.Core`.

#### Scenario: User profile and credentials stored separately

- **WHEN** a user is created
- **THEN** a profile row holds `id`, `email`, `email_normalized`, `name`,
  `global_role`, `enabled`, `force_password_reset`, `mfa_enabled`, `created_at`,
  `updated_at`, and the password hash is stored in `user_password_credentials`
  keyed by the user id

#### Scenario: Ids are sortable ULIDs

- **WHEN** two users are created in sequence
- **THEN** their `id` values are ULIDs whose lexical (binary-collation) order matches
  creation-time order

#### Scenario: Core gains no auth dependency

- **WHEN** the user store and its hashing dependency are added
- **THEN** `Freeboard.Core` still references no `System.Net.Http` or
  `System.Net.Sockets` types and gains no database client reference

### Requirement: Normalized email uniqueness

The system SHALL store a normalized email (trimmed, lowercased with the invariant
culture) in `email_normalized` with a UNIQUE index, and SHALL resolve users by the
normalized value so addresses differing only in case or surrounding whitespace
resolve to the same account. Uniqueness SHALL NOT depend on a case-insensitive
column collation; the normalized column is the explicit key. The original `email`
is retained for display.

#### Scenario: Duplicate email rejected by normalization

- **WHEN** a user is created with an email that normalizes to an existing
  `email_normalized` value
- **THEN** the creation fails and no second row is stored

#### Scenario: Login lookup uses the normalized email

- **WHEN** a user registered as `[email protected]` logs in as ` [email protected] `
- **THEN** the input is normalized and the same account is resolved

### Requirement: Passwords hashed with Argon2id and a keyed secret

Passwords SHALL be hashed with Argon2id (a memory-hard KDF) and SHALL NEVER be
stored in plaintext or in a reversible form. Each hash SHALL use a unique random salt
and SHALL be stored in a self-describing encoded form recording the algorithm and
parameters, so parameters can be raised later and existing hashes upgraded on the
next successful login. A server-side secret (pepper) SHALL be REQUIRED, supplied
out-of-band (environment, user-secrets, or config provider), and mixed in as the
Argon2 KEYED secret parameter (not naive concatenation), so a database-only
compromise cannot offline-attack the hashes. The secret SHALL NOT be committed or
stored in the database. Only the web app hashes passwords; the CLI does not, so no
secret is shared with the CLI.

#### Scenario: Password stored only as a keyed, salted hash

- **WHEN** a user is created with a password
- **THEN** the stored hash is a salted Argon2id hash computed with the keyed secret,
  and the plaintext password is not recoverable from the database

#### Scenario: Distinct salts for identical passwords

- **WHEN** two users choose the same password
- **THEN** their stored hashes differ because each uses a unique random salt

### Requirement: Account enable and force-password-reset state

The store SHALL support disabling an account and flagging an account for a forced
password reset. A disabled account SHALL NOT be able to authenticate. A user with
`force_password_reset` set SHALL be required to set a new password before normal use.

#### Scenario: Disabled user cannot authenticate

- **WHEN** a disabled user submits correct credentials
- **THEN** authentication fails

#### Scenario: Forced reset flagged on the user

- **WHEN** an administrator flags a user for a forced password reset
- **THEN** the user's `force_password_reset` is true until a new password is set

### Requirement: First-admin bootstrap via the API

The web API SHALL provide `POST /api/v1/freeboard/setup` that creates the first
administrator account. It SHALL succeed ONLY while a one-time bootstrap secret
(REQUIRED, supplied out-of-band as configuration) is presented and no admin has yet
been created. First-admin creation SHALL be guarded by a SINGLE-ROW SENTINEL TABLE
(`bootstrap_marker` with a fixed primary key): inside one transaction the path SHALL
FIRST `INSERT INTO bootstrap_marker (id) VALUES (1)` and rely on the primary-key
collision so that exactly one concurrent caller's insert succeeds; the loser's
duplicate-key failure SHALL be returned as `409`. Only the winning caller SHALL
proceed, in the same transaction, to create the admin user. (A named advisory lock or
`SELECT ... FOR UPDATE` on the sentinel row is an acceptable equivalent guard.) Once
the marker exists (an admin was created) the endpoint SHALL return `409` and create
nothing (self-disabling). A missing or wrong bootstrap secret SHALL return `401` and
SHALL NOT open the transaction. The endpoint SHALL be rate-limited. On success it SHALL
return the created admin and an admin session token.

#### Scenario: Bootstrap creates the first admin

- **WHEN** `POST /api/v1/freeboard/setup` is called before any admin has been created
  with the correct bootstrap secret
- **THEN** the sentinel insert succeeds, the first administrator is created, and an
  admin token is returned

#### Scenario: Concurrent bootstrap yields exactly one admin

- **WHEN** two `POST /api/v1/freeboard/setup` requests with the correct secret race
  before any admin exists
- **THEN** exactly one sentinel insert wins and creates the admin, the other request
  gets `409`, and no second user exists

#### Scenario: Bootstrap self-disables after the first admin

- **WHEN** `POST /api/v1/freeboard/setup` is called after the first admin (and its
  sentinel row) already exists
- **THEN** the sentinel insert collides, the response is `409`, and no user is created

#### Scenario: Bootstrap rejects a wrong secret

- **WHEN** `POST /api/v1/freeboard/setup` is called before any admin exists without
  the correct bootstrap secret
- **THEN** the response is `401` and no user is created
