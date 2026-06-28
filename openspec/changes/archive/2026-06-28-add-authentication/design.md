## Context

This change adds authentication to the Freeboard web app and CLI. The goal is
FEATURE parity with mature device-management tooling (login, opaque bearer tokens,
sessions, the password lifecycle, and MFA) - NOT interface or byte parity with any
specific product. The REST shapes, paths, and field names are designed for Freeboard
using clean, conventional REST conventions; they are not copied from another tool.
(An earlier revision of this design mirrored FleetDM's paths and bodies under a
"documented divergences" framing; that framing is removed - we offer equivalent
capabilities with a Freeboard-native API.)

The change builds on the archived persistence layer, unchanged:

- `src/Freeboard.Persistence` (MIT) holds all DB code and the Dapper + MySqlConnector
  dependency. `Freeboard.Core` references nothing; a structural test asserts the Core
  assembly references no `System.Net.Http`/`System.Net.Sockets` types, so no
  DB/socket/crypto-native dependency may land in Core.
- `IDbConnectionFactory` opens connections from `PersistenceOptions.ConnectionString`.
  Stores use hand-written SQL via Dapper. The `001_initial_schema` tables use
  binary-collation STRING ids (`VARCHAR(190) COLLATE utf8mb4_bin`) with explicit `KEY`
  index definitions on FK columns. The new auth tables follow the same
  binary-collation string-id convention.
- Migrations are embedded `Migrations/NNN_slug.sql`, applied by `MySqlMigrationRunner`
  (`IMigrationRunner.ApplyPendingAsync`/`GetStateAsync`) with a `schema_migrations`
  checksum table. New tables ship as `002`/`003` via `system migrate`.
- DI registers the connection factory once via `TryAddSingleton`. The web app never
  auto-connects at startup; an unreachable store surfaces per request.
- CLI commands resolve the connection string via `ConnectionStringResolver`
  (`--connection-string` overrides `FREEBOARD_DB`); exit codes `0` success, `1`
  input/validation error, `3` operational failure.
- VERIFIED: `src/Freeboard/GitOps/GitOpsReadOnlyMiddleware.cs` rejects ALL of
  POST/PUT/PATCH/DELETE with `409` when `GitOpsOptions.ReadOnly` is on, and runs
  before any explicit routing (Program.cs has no `app.UseRouting()` today). Also
  VERIFIED: `CLAUDE.md` omits `Freeboard.Persistence`.

## Goals / Non-Goals

**Goals:**

- A clean Freeboard-native auth API under a single `/api/v1/freeboard/*` namespace,
  covering login, sessions, the password lifecycle, MFA, and step-up/sudo-mode.
- Best-practice user storage: Argon2id with a keyed secret (no plaintext), normalized-
  email uniqueness, credentials split from the profile, ULID ids.
- Opaque, revocable, server-side bearer session tokens (not JWT).
- MFA with passkeys, TOTP, recovery codes, and an emailed magic-link fallback.
- Storage-agnostic hardening seams (rate limiting, challenge store, session store) so
  a Redis backend can replace MySQL at multi-server scale.
- A CLI that administers users through the authenticated API, with a safe first-admin
  bootstrap path; only `system migrate` touches the DB directly.
- All new code MIT; Core stays network-free; CLI/Agent stay EE-free and cross-platform.

**Non-Goals:**

- SSO (SAML/OIDC) - future / EE. Authenticated reconciling `apply` - stays dry-run.
  Flipping existing public endpoints to require auth. Web UI login pages. A concrete
  SMTP transport (the email seam is built; transport is operator config). Fine-grained
  RBAC beyond a coarse role. Personal access tokens. Multi-tenant partitioning. Agent
  changes. See proposal Non-goals.

## Decisions

### D1. Password hashing: Argon2id via Konscious with a keyed secret (investigated)

Investigation (alternatives weighed for net10, MIT, cross-platform, security):

- `Konscious.Security.Cryptography.Argon2` (chosen): MIT, pure-managed
  (netstandard1.3, runs on net10), no native dependency, supports Argon2id and a
  `KnownSecret` (the Argon2 native "secret" / keyed parameter). Cross-platform with
  zero native baggage.
- `NSec.Cryptography` (libsodium-backed Argon2id, audited constant-time native impl):
  REJECTED for this repo PRIMARILY because it carries a pinned NATIVE `libsodium`
  dependency (verified on NuGet: >= 1.0.22, < 1.0.23). A pinned native lib is a
  cross-platform packaging and version-alignment liability - it must ship a matching
  native binary per RID and stay pinned to a narrow libsodium range - which is exactly
  the kind of native/version coupling the persistence layer already avoided. (Its
  current release also targets net9.0 rather than net10, a secondary alignment point,
  but the native-dependency burden is the deciding factor.) The
  audited native implementation is attractive, but the lack of a net10-aligned release
  and a pinned native lib outweighs it here. Recorded so it can be reconsidered once a
  net10-aligned NSec ships.
- `Isopoh.Cryptography.Argon2` (pure-managed, supports the Argon2 secret parameter):
  REJECTED on license - it is CC-BY 4.0, not MIT, which does not fit the repo's MIT
  default.
- Built-in ASP.NET Core PBKDF2 `PasswordHasher<T>`: recorded fallback (zero
  dependency, OWASP-acceptable) behind the `IPasswordHasher` seam, but PBKDF2 is not
  memory-hard, so Argon2id is preferred.

Conclusion: Konscious Argon2id is the most secure option that is simultaneously MIT,
runs on net10 (it targets netstandard1.3), and native-dependency-free for this repo.
Parameters (OWASP 2024,
config-tunable): memory 19 MiB, iterations 2, parallelism 1, 16-byte random salt,
32-byte output, self-describing PHC encoding.

Peppering construction (strongest available): use Argon2's native KEYED `Secret`
parameter (`Konscious` `KnownSecret`) rather than naive concatenation. The pepper is
mixed into the KDF as the algorithm's secret key, so it is part of the memory-hard
computation - a DB-only compromise cannot offline-attack hashes without the secret,
and there is no length-extension or concatenation-ambiguity weakness that a
`pepper||password` string would have. The secret is REQUIRED, supplied out-of-band
(env/user-secrets/config), versioned (for rotation), and is only needed by the WEB app
(the CLI no longer hashes - see D13). Hashes are self-describing so parameters/secret-
version can be raised and old hashes upgraded on next successful login. The
`IPasswordHasher` seam is kept so the hasher (or the PBKDF2 fallback) can be swapped
without touching call sites. Rejected: BCrypt, home-rolled hashing, reversible/unsalted
forms, and `pepper||password` string concatenation in favour of the keyed Secret.

### D2. Token format: opaque tokens with a key-id prefix, keyed HMAC at rest; not JWT

A 32-byte CSPRNG opaque secret, returned once, never JWT. Opaque server-side tokens
are chosen on their own merits: they are revocable server-side at any moment (logout,
session-delete, password change), carry no client-readable claims to leak or tamper
with, avoid signing-key/alg-confusion classes of bugs entirely, and need only an
indexed primary-key lookup per request. A JWT could not be revoked before expiry
without a server-side denylist - reintroducing the same DB lookup while adding crypto
surface for no benefit. Rejected: JWT/JWE.

At-rest hashing: store a KEYED HMAC-SHA256 of the secret (not bare SHA-256), so a
dumped table cannot be turned into a lookup table or confirm a guessed token without
the server key.

Key rotation. `FREEBOARD_AUTH_TOKEN_KEYS` is a versioned HMAC key set (current key id
+ retained older keys), used by token type:

- PREFIX-BEARING tokens - session, password-reset, AND the MFA challenge token - are
  minted as `v<keyId>.<secret-base64url>`. On validation the handler parses the `keyId`
  FROM THE TOKEN, selects the key, computes the HMAC, and looks the row up by
  `token_hash`. It then asserts the stored `token_key_version` equals the parsed key id
  as an integrity check, treating a mismatch as invalid (`401`/not found). New tokens
  are minted under the current key id; existing tokens keep validating under whatever
  key id their prefix names until they expire/are revoked.
- PREFIXLESS tokens - human-typed recovery codes AND the emailed magic-link token -
  carry NO `v<keyId>.` prefix, so the signing key id is read from a STORED key-version
  column, not parsed from the input: recovery codes from
  `mfa_recovery_codes.token_key_version`, and the magic-link token from
  `mfa_login_challenges.magic_link_token_key_version` (the magic-link token is a
  separate secret minted on send, not at challenge creation). Verification HMACs the
  entered/emailed value with the key named by that stored column. Each keeps verifying
  after a rotation because its stored key version still names a retained key.

Malformed or unknown-key tokens are rejected with a uniform `401` and NO DB lookup
(no valid hash can be computed), not revealing which condition failed.

### D3. Session storage, lifecycle, and step-up (sudo-mode) state

`sessions`: `id` CHAR(26) PK (utf8mb4_bin ULID), `user_id` CHAR(26) NOT NULL FK ->
`users(id)` ON DELETE CASCADE, `token_hash` BINARY(32) NOT NULL UNIQUE (keyed HMAC;
lookup key), `token_key_version` INT NOT NULL, `auth_state` TINYINT NOT NULL (0 =
full, 1 = force-reset-limited), `sudo_at` DATETIME(6) NULL (the last successful step-up
/ reauth on this session; see D9), `created_at` DATETIME(6) NOT NULL, `expires_at`
DATETIME(6) NOT NULL, `last_seen_at` DATETIME(6) NULL. Explicit `KEY (user_id)`.

Expiry: config-driven with a documented DEFAULT fixed 24h. Sliding/inactivity expiry
and "remember me" are a future option.

Lifecycle: create on a fully authenticated login (after MFA if enrolled); validate per
request (parse key id, HMAC lookup, integrity-assert key version, reject
missing/expired/revoked/disabled-user); bump `last_seen_at`; revoke on logout (delete
the row); bulk-revoke `WHERE user_id = @u` for "log out everywhere" and on password
change/reset. Expired rows pruned opportunistically; FK cascade removes sessions when
a user is deleted.

Force-reset-limited sessions: a `force_password_reset` user logs in with
`auth_state = 1`; the bearer handler permits only `GET /auth/me`, `POST /auth/logout`,
and `POST /account/password` (D10). On a successful `POST /account/password` the SAME
session is upgraded IN PLACE (`auth_state = 0`); the token is unchanged.

Session-management endpoints (clean REST): `GET /auth/sessions/{id}` (metadata, no
token), `DELETE /auth/sessions/{id}`, `GET /users/{id}/sessions`,
`DELETE /users/{id}/sessions`. A non-admin may read/delete ONLY their own sessions; a
non-owned target returns `404` (not `403`) so existence is not disclosed; an admin may
act cross-user. `{id}` params are ULID strings.

Redis-swappable: the session store sits behind `ISessionStore` (D8 / D14); its methods
are storage-agnostic so a Redis-backed session store could replace the MySQL one. The
DB-backed store is the default.

### D4. WebAuthn/FIDO2: fido2-net-lib in the web app; explicit RP/origin; synced-passkey counter

`Fido2`/`Fido2.AspNet` (passwordless-lib, MIT). The web app runs the ceremonies (the
library needs `HttpContext`); `Freeboard.Persistence` only stores credential rows.
Registration verifies an attestation and persists the credential; the assertion step
verifies against the stored public key and updates the counter. User verification is
REQUIRED. RP id and allowed origins are EXPLICIT REQUIRED config outside dev; forwarded
host/proto honored only via a configured trusted proxy; registration and assertion
reject a mismatched origin or RP-id hash (negative tests required).

`webauthn_credentials`: `id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK CASCADE,
`credential_id` VARBINARY(255) NOT NULL UNIQUE, `public_key` VARBINARY(1024) NOT NULL,
`sign_count` BIGINT UNSIGNED NOT NULL, `user_handle` VARBINARY(64) NOT NULL, `aaguid`
CHAR(36) NULL, `transports` VARCHAR(255) NULL, `cred_type` VARCHAR(32) NULL,
`is_backup_eligible` TINYINT(1) NULL, `is_backed_up` TINYINT(1) NULL, `nickname`
VARCHAR(190) NULL, `created_at` DATETIME(6) NOT NULL, `last_used_at` DATETIME(6) NULL,
explicit `KEY (user_id)`. (`aaguid` stays CHAR(36): it is a vendor-assigned UUID from
the authenticator, not a Freeboard-generated id.)

Sign-counter rule: never reject a counter of 0 (synced passkeys report and keep 0);
reject a regression only when BOTH stored and presented counters are > 0 and the
presented value does not exceed the stored value.

### D5. TOTP: Otp.NET, AES-256-GCM secret at rest, atomic replay guard

`Otp.NET` (MIT, RFC 6238). Pure managed, lives in `Freeboard.Persistence`. SHA-1, 6
digits, 30s step, +/-1 window. The per-user secret is ENCRYPTED at rest (verification
needs plaintext): AES-256-GCM via `ISecretProtector` with a REQUIRED out-of-band key;
ciphertext, nonce, tag, and a `key_version` stored. Enrollment returns the otpauth://
URI and activates only after a confirming code. Replay: conditional
`UPDATE ... WHERE last_time_step < @step` (atomic).

`totp_credentials`: `user_id` CHAR(26) PK/FK CASCADE, `secret_ciphertext`
VARBINARY(255) NOT NULL, `secret_nonce` VARBINARY(12) NOT NULL, `secret_tag`
VARBINARY(16) NOT NULL, `key_version` INT NOT NULL, `confirmed_at` DATETIME(6) NULL,
`last_time_step` BIGINT NULL, `created_at` DATETIME(6) NOT NULL.
`mfa_recovery_codes`: `id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK CASCADE,
`code_hash` BINARY(32) NOT NULL (keyed HMAC, D2), `token_key_version` INT NOT NULL,
`used_at` DATETIME(6) NULL, `created_at` DATETIME(6) NOT NULL, explicit `KEY (user_id)`.
Recovery codes: 10 high-entropy, shown once, consumed atomically; ALWAYS a valid
factor; regenerate replaces the set (re-signed under the current key id).

### D6. MFA login flow: DB-backed hashed challenge, max attempts, factor set

MFA is OPTIONAL per user. When a user with at least one MFA factor logs in with
correct credentials, the system returns `202` with a single-purpose, short-lived MFA
challenge and the available factors, e.g.
`{ "mfa_required": true, "mfa_token": "...", "factors": ["passkey","totp","recovery"] }`
(plus `"magic_link"` per D7). The `mfa_token` is presented in the request BODY of the
verify endpoints, never as a session bearer; the bearer handler rejects it everywhere.

Verify endpoints (clean REST under `/auth/mfa/`): `POST /auth/mfa/totp`,
`POST /auth/mfa/passkey/options` + `POST /auth/mfa/passkey`,
`POST /auth/mfa/recovery`, and `POST /auth/mfa/magic-link/*` (D7). Each consumes a
valid unexpired unconsumed challenge and on success returns `200 { user, token }`.

Challenge storage: `mfa_login_challenges` (`id` CHAR(26) PK, `challenge_token_hash`
BINARY(32) NOT NULL UNIQUE keyed HMAC, `token_key_version` INT NOT NULL, `user_id`
CHAR(26) NOT NULL FK CASCADE, `factors` VARCHAR(64) NOT NULL, `webauthn_options` JSON
NULL, `magic_link_token_hash` BINARY(32) NULL (set only when a link was sent, D7),
`magic_link_token_key_version` INT NULL (the HMAC key the sent magic-link token was
hashed under; NULL until a link is sent - see F-34/D2), `magic_link_expires_at`
DATETIME(6) NULL, `magic_link_sends` INT NOT NULL DEFAULT 0 (re-send counter, F-41),
`expires_at` DATETIME(6) NOT NULL, `consumed_at` DATETIME(6) NULL, `attempts` INT NOT
NULL DEFAULT 0, `created_at` DATETIME(6) NOT NULL, explicit `KEY (user_id)`). Survives
restarts and multiple web instances. Max attempts: each failed verify atomically
increments `attempts`; after 5 the row is consumed and the user restarts at login.
MFA-verify endpoints are also covered by the rate limiter (D11). The challenge store
sits behind `IMfaChallengeStore` and is Redis-swappable (D14).

Enrollment (authenticated) under `/auth/mfa/`: `GET /auth/mfa` (status); TOTP
`enroll`/`activate` + delete; passkey `options`/register + per-credential delete;
`recovery/regenerate`. Maintain `users.mfa_enabled` as factors are activated/removed;
return recovery codes once on the first factor. All MFA-state changes are guarded by
sudo-mode (D9).

### D7. Magic-link fallback MFA factor

A magic link is an MFA factor offered as a FALLBACK only when the account has neither a
passkey nor TOTP enrolled AND an email sender is configured. It lets an
MFA-enabled-but-no-strong-factor user still complete a second step. (`users.email` is
NOT NULL, so every account has an email; the only runtime gate is the email-sender
configuration, F-38.)

Policy:

- MFA is optional per user. A user with no factors logs in directly (no second step).
  Magic-link is NOT enrolled; it is automatically available as the fallback when
  `users.mfa_enabled` is true but the user has no passkey and no TOTP AND an email
  sender is configured. In that state the login challenge's `factors` list includes
  `magic_link`.
- `POST /auth/mfa/magic-link/send` (with the `mfa_token`) emails a single-use,
  short-TTL magic-link token. `POST /auth/mfa/magic-link/verify` (with the `mfa_token`
  and the emailed token) completes the challenge -> `200 { user, token }`.
- The emailed token reuses the keyed-HMAC-at-rest + DB challenge-row pattern, but it is
  a SEPARATE, PREFIXLESS secret from the challenge token and is minted later (on
  `send`), so it carries NO `v<keyId>.` prefix and instead has its OWN stored key
  version (F-34, exactly like recovery codes): on `send` the token is hashed under the
  CURRENT HMAC key and that key id is stored in `magic_link_token_key_version`; on
  `verify` the presented magic-link token is HMACed with the key NAMED BY THAT STORED
  COLUMN (not parsed from the input) and compared against `magic_link_token_hash`.
  Single-use, short TTL (`magic_link_expires_at`), attempt-capped like other factors.
- Re-send throttling (F-41): each `send` atomically increments `magic_link_sends` and
  is rejected once a per-challenge cap (e.g. 3) is reached; `send` is also covered by
  the per-account/per-IP rate limiter (D11), so a magic link cannot be used to
  email-bomb a target.
- No-sender behavior: if no email sender is configured, `magic_link` is NOT offered and
  the send endpoint returns a clear error. A user with MFA enabled, no strong factor,
  and no configured sender must use a recovery code; an admin MFA-reset is a future
  flow.

Email seam: add a NET-NEW `IAuthEmailSender` interface (there is no prior notifier to
generalize) with a method per email kind (password reset, magic link). The concrete
transport is operator config; this change ships the seam, not an SMTP stack. If
password reset is enabled with no sender, the app fails fast at startup (D12);
magic-link is simply not offered without a sender.

### D8. User schema, normalized email, split credentials, ULID ids

ULID ids (D15): all Freeboard-generated ids are ULIDs stored as Crockford base32
`CHAR(26) COLLATE utf8mb4_bin`.

Email uniqueness: a separate `email_normalized` column (trim + lower, invariant
culture) with a UNIQUE binary index is the explicit lookup/uniqueness key; `email` is
kept for display. Credentials placement: `password_hash` lives in
`user_password_credentials`, not on the profile.

`users`: `id` CHAR(26) PK utf8mb4_bin (ULID), `email` VARCHAR(190) NOT NULL,
`email_normalized` VARCHAR(190) NOT NULL UNIQUE (binary), `name` VARCHAR(255) NOT NULL,
`global_role` VARCHAR(32) NOT NULL, `enabled` TINYINT(1) NOT NULL DEFAULT 1,
`force_password_reset` TINYINT(1) NOT NULL DEFAULT 0, `mfa_enabled` TINYINT(1) NOT NULL
DEFAULT 0, `created_at` DATETIME(6) NOT NULL, `updated_at` DATETIME(6) NOT NULL.
`user_password_credentials`: `user_id` CHAR(26) PK/FK CASCADE, `password_hash`
VARCHAR(255) NOT NULL (PHC Argon2id), `secret_version` INT NOT NULL (the Argon2
keyed-secret version, for secret rotation), `updated_at` DATETIME(6) NOT NULL.
`password_reset_tokens`: `id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK CASCADE,
`token_hash` BINARY(32) NOT NULL UNIQUE (keyed HMAC), `token_key_version` INT NOT NULL,
`expires_at` DATETIME(6) NOT NULL, `used_at` DATETIME(6) NULL, `created_at` DATETIME(6)
NOT NULL, explicit `KEY (user_id)`.
`bootstrap_marker`: `id` TINYINT NOT NULL PRIMARY KEY, `created_at` DATETIME(6) NOT
NULL. A single-row sentinel; the `setup` path inserts the fixed `id = 1` to win the
first-admin race by PK collision (D13/F-32).

Auth stores live in `Freeboard.Persistence.Auth` (hand-written-SQL Dapper):
`IUserStore`, `IPasswordCredentialStore`, `ISessionStore`, `IPasswordResetStore`,
`ITotpStore`, `IWebAuthnCredentialStore`, `IRecoveryCodeStore`, `IMfaChallengeStore`,
`IAuthRateLimitStore`, plus `IPasswordHasher`, `ITokenHasher`, `ISecretProtector`,
`IUlidFactory`, `IAuthEmailSender`. Registered via ONE aggregate
`AddAuth(connectionString)` that registers the connection factory once.

### D9. Sudo-mode (step-up auth)

Generalize the "mandatory reauth for MFA-state changes" into a reusable sudo-mode any
endpoint can opt into.

- The session carries `sudo_at` (D3). A caller ENTERS sudo-mode via `POST /auth/sudo`,
  which re-confirms ANY of the user's currently-usable factors - the SAME factor set
  the login MFA challenge would accept for that user: passkey, TOTP, recovery code, OR
  the magic-link fallback (when applicable), for MFA users; or a password re-confirm
  for users without MFA (F-35). This means a magic-link-only user can satisfy sudo via
  magic-link and then enroll a passkey/TOTP - they are NOT locked out of repairing
  their MFA. On success it stamps `sessions.sudo_at = now`. The endpoint is
  rate-limited (D11).
- A `RequireSudoMode` authorization policy / endpoint filter checks
  `sudo_at IS NOT NULL AND sudo_at > now - TTL` (TTL config-driven, default 5 minutes)
  and returns `403` with a clear "step-up required" body otherwise. Any endpoint opts
  in by carrying the policy/marker; the pipeline enforces it after bearer auth.
- Consumers this increment: all MFA-state mutations (enroll/activate/remove factor,
  regenerate recovery codes) require sudo-mode. The mechanism is generic so future
  sensitive endpoints (real `apply`, user deletion) can add the marker.
- `change_password` is NOT sudo-gated: it already requires `old_password`, the
  proof-of-presence.

### D10. Password lifecycle endpoint naming

Clean Freeboard-native names under `/api/v1/freeboard/`:

- `POST /auth/password/change` - logged-in user changes password
  (`{ old_password, new_password }`).
- `POST /auth/password/forgot` - request a reset (`{ email }`), always uniform `200`.
- `POST /auth/password/reset` - complete a reset with an emailed token
  (`{ token, new_password }`).
- `POST /account/password` - a force-reset-limited user sets a new password
  (`{ new_password }`). This REPLACES the unwieldy `perform_required_password_reset`;
  it clears `force_password_reset` and upgrades the session in place (D3).

### D11. Rate limiting: separate atomic buckets, enumeration-safe, storage-agnostic

SEPARATE per-account (normalized email) and per-IP buckets, each atomically checked
and incremented; a request is limited if ANY applicable bucket trips, returning `429` +
`retry-after`. The limiter covers the login, the MFA-verify (`/auth/mfa/*` incl.
magic-link send), AND the step-up (`POST /auth/sudo`) endpoints, since each checks a
factor or password and would otherwise be an online guessing oracle (F-40). Trusted
client IP: socket remote IP unless via a configured trusted proxy (ASP.NET Core
forwarded headers with KnownProxies/KnownNetworks). Enumeration
safety: the account bucket locks unknown emails too, so a `429` is uniform and not an
oracle. Reset on success: ONLY the account bucket resets; the IP bucket persists.
Retention: stale, no-longer-locked rows are pruned (retention >= longest lockout).

Storage-agnostic interface (D14): `IAuthRateLimitStore` exposes storage-agnostic
operations - an atomic check-and-increment returning the current state / retry-after, a
reset(accountKey), and a prune(retention) - and does NOT leak SQL. The default impl is
MySQL (composite PK `(bucket_kind, bucket_key)`, atomic upsert increment); a Redis-
backed impl (INCR + EXPIRE, or a Lua check-and-increment) can replace it behind the
same seam.

### D12. GitOps read-only middleware: namespace move + scoped auth exemption

The middleware currently rejects every mutating method with `409` in read-only mode,
which would block all auth (all POST). Fix: scope an exemption to the specific auth
ENDPOINTS via an `AuthEndpoint` metadata marker the middleware inspects, NOT the whole
prefix. Because `context.GetEndpoint()` is null until routing runs and Program.cs has
no explicit `app.UseRouting()`, ADD an explicit `app.UseRouting()` BEFORE
`app.UseMiddleware<GitOpsReadOnlyMiddleware>()`, leaving the endpoint mappings after it;
the middleware reads `context.GetEndpoint()?.Metadata.GetMetadata<AuthEndpoint>()` and
skips its `409` only for marked auth endpoints. Non-auth mutating routes (including
non-auth routes under `/api/v1/freeboard/`) still get `409`.

Namespace move: the GitOps status endpoint moves to
`GET /api/v1/freeboard/gitops/status`. This MODIFIES the `gitops-readonly-ui`
capability (the status path changes AND the read-only requirement narrows to exempt
marked auth endpoints).

### D13. CLI administers users via the HTTP API; first-admin bootstrap; only migrate touches the DB

The CLI does NOT use the persistence user store and does NOT touch the DB for `user`
commands; they call the authenticated HTTP API.

- An `IFreeboardApiClient` (in the CLI) calls `/api/v1/freeboard/*` over HTTP with an
  admin bearer token. Token source: a `--token` option or `FREEBOARD_ADMIN_TOKEN` env
  var; base URL via `--api-url` or `FREEBOARD_API_URL`. Exit codes `0`/`1`/`3` per the
  existing convention (`1` input/validation, `3` operational/HTTP failure incl. `401`/
  `403`/`5xx`).
- Admin user-management endpoints (the `user-admin` capability, consumed by the CLI),
  all under `/api/v1/freeboard/` and all behind the admin authorization policy
  (non-admin -> `403`; ULID `{id}` params; `422` on validation errors):
  - `POST /users` (create `{ email, name, global_role }`; the server generates a
    cryptographically random ONE-TIME temporary password, stores its Argon2id hash,
    sets `force_password_reset = true`, and returns `{ user, temporary_password }` with
    the ULID id - the temp password is returned ONCE, never stored plaintext, never
    re-retrievable; no email is sent; `422` on duplicate email / invalid input).
  - `GET /users` (list; no password) and `GET /users/{id}` (one; `404` if absent).
  - `POST /users/{id}/disable` and `POST /users/{id}/enable` (set enabled state;
    disabling revokes that user's sessions).
  - `POST /users/{id}/reset-password` (generates a new ONE-TIME temporary password,
    stores its Argon2id hash, sets `force_password_reset = true`, revokes the user's
    sessions, and returns `{ temporary_password }` once; no email is sent).
  Credential handoff is in-band (the returned temp password), NOT by email: the user
  logs in with it - forced into the limited session (D3) - and sets a new one via
  `POST /account/password` (D10). Email is only used by password/forgot and magic-link.
  The CLI `user create/list/disable/enable/reset-password` map one-to-one to these. No
  DB access, no Argon2, no shared pepper - only the WEB app hashes, so the CLI/web
  shared-pepper coupling is REMOVED.
- First-admin bootstrap (chicken-and-egg), guarded by a SENTINEL TABLE (F-32): the API
  exposes `POST /api/v1/freeboard/setup` that creates the first admin. Concurrency is
  guarded by a dedicated single-row sentinel table `bootstrap_marker` (a fixed PK).
  Inside ONE transaction the bootstrap path FIRST runs `INSERT INTO bootstrap_marker
  (id) VALUES (1)`: the primary-key collision means exactly one concurrent caller's
  insert succeeds and the loser's insert raises a duplicate-key error -> the endpoint
  maps that to `409`. ONLY the winner proceeds, in the SAME transaction, to insert the
  admin user and commit. This is a reliable MySQL concurrency guard (a unique-key
  collision under InnoDB), unlike a check-then-insert. The marker insert is the sole
  bootstrap invariant: it succeeds exactly once (the first admin), and because the
  marker persists every later `setup` call collides and returns `409` (self-disabling).
  The endpoint REQUIRES a one-time bootstrap secret (`FREEBOARD_BOOTSTRAP_SECRET`,
  supplied out-of-band): a wrong/absent secret returns `401` and never opens the
  transaction; it is rate-limited. (Acceptable equivalents, if preferred at implementation time: a named
  advisory lock `GET_LOCK('freeboard_bootstrap', ...)` around the check-and-insert, or
  `SELECT ... FOR UPDATE` on the sentinel row; the sentinel-PK-collision form is the
  chosen baseline.) The CLI offers `user bootstrap` that calls it
  and prints the returned admin token. The operator then uses that token (or logs in)
  for further `user` commands.
- Only `system migrate` keeps direct DB access (schema is a platform concern, runs
  before the API can serve). The CLI keeps the `Freeboard.Persistence` reference ONLY
  for `system migrate`; the `user` group references only the HTTP client.

### D14. Redis-swappable hardening seams

Three hardening stores are designed storage-agnostic so a Redis impl can drop in for
multi-server scale, with the MySQL impl as the default:

- `IAuthRateLimitStore` (D11): atomic check-and-increment / reset / prune; no SQL in
  the contract. Redis: INCR/EXPIRE or a Lua script.
- `IMfaChallengeStore` (D6): create / find-by-hash / atomic-increment-attempts /
  consume; short-lived rows. Redis: hashed keys with TTL.
- `ISessionStore` (D3): create / find-by-token-hash / get-by-id / list-by-user /
  delete / set-sudo / prune. Redis-swappable, though sessions are the least urgent to
  move (list-by-user is a secondary index in Redis).

The MFA-credential, user, and password-reset stores remain MySQL (durable user data,
not hot-path/ephemeral). The seams keep contracts free of Dapper/SQL types.

### D15. ULID ids (investigated)

Use ULIDs instead of UUIDs for all generated ids (user, session, credential, token,
challenge row ids). ULIDs are 128-bit, lexically sortable, and time-ordered, which
keeps inserts roughly append-ordered on the binary-collated PK (better index locality
than random UUIDv4) and makes ids sortable by creation time without a separate column.

Library (investigated): `Ulid` by Cysharp (MIT, no native dependency; targets
netstandard2.0 / net8.0 and runs on net10 as computed-compatible; provides 16-byte and
Crockford base32 conversions). Chosen over hand-rolling and over `NUlid`/others on
adoption and the pure-managed, no-native-dependency story. Lives in
`Freeboard.Persistence` behind an `IUlidFactory` seam (so id generation is
testable/deterministic in tests).

Storage form (investigated - CHAR(26) Crockford base32 vs BINARY(16)): store as
`CHAR(26) COLLATE utf8mb4_bin`. Rationale: (1) it preserves the existing schema
convention - `001_initial_schema` uses binary-collation STRING ids, and the auth
stores already key on string ids; (2) Crockford base32 is lexically sortable, so
`CHAR(26)` binary-collation sort == ULID time order, retaining sortability without the
opacity of BINARY(16); (3) human-readable in queries, logs, and URLs (route params),
which matters for an admin API and CLI; (4) avoids byte-order pitfalls of storing
128-bit values as BINARY(16). The ~10-byte-per-id storage cost over BINARY(16) is
acceptable for these row counts. BINARY(16) (more compact, still sortable if stored
big-endian) is recorded as the alternative if id volume ever makes storage the
bottleneck; the `IUlidFactory` seam and CHAR columns localize a future change.

This supersedes the earlier "keep UUID" decision, which was made under the now-removed
interface-parity assumption.

### D16. API namespace move (breaking)

All web API routes move under one `/api/v1/freeboard/*` prefix via a single
`ApiRoutePrefix` constant (the auth group shares it). The existing endpoints move too:

- `GET /api/standards` -> `GET /api/v1/freeboard/standards`
- `GET /api/controls` -> `GET /api/v1/freeboard/controls`
- `GET /api/scopes` -> `GET /api/v1/freeboard/scopes`
- `GET /api/compliance/status` -> `GET /api/v1/freeboard/compliance/status`
- `GET /api/gitops/status` -> `GET /api/v1/freeboard/gitops/status`

This is a BREAKING path change for those endpoints (they have no auth/clients in
production yet, so the blast radius is small). An OPTIONAL 308 redirect shim from the
old paths to the new can be added for one release; this change documents the move and
removes the old paths (the shim is left to the implementer's discretion and is not a
required behavior in the specs). MODIFIES `compliance-web-read` and `gitops-readonly-ui`.

## Risks / Trade-offs

- [Security-critical hand-wiring] -> vetted libraries (Argon2 keyed-secret, TOTP,
  fido2-net-lib, Ulid); keyed HMAC for all server-issued/verified secrets; generic
  `401` with constant-work verify for unknown/disabled accounts; uniform forgot-
  password `200`; required out-of-band keys; no DIY crypto.
- [Password KDF native-vs-managed] -> chose pure-managed Konscious Argon2id with a
  keyed Secret; libsodium-backed NSec rejected only on net10/native-dep alignment, not
  cryptographic merit, revisit when net10-aligned (D1).
- [Account enumeration] -> uniform `401` + constant-work hashing; uniform forgot-
  password; `429` locks unknown emails; session IDOR returns `404`.
- [Stolen session abuse] -> force-reset-limited sessions; sudo-mode gates sensitive
  changes; MFA challenge token never a bearer; opaque revocable tokens.
- [Magic-link as a weaker factor] -> offered ONLY as a fallback when no stronger factor
  exists and email is configured; single-use, short TTL, keyed-hash at rest, attempt-
  capped; documented as the weakest factor.
- [Email seam without a transport] -> `IAuthEmailSender` is a seam; reset fails fast at
  startup if enabled with no sender; magic-link not offered without a sender.
- [API move is breaking] -> small blast radius (no production clients); optional 308
  shim for one release; documented in MODIFIED specs (D16).
- [CLI-via-API bootstrap] -> the `setup` endpoint is a sensitive unauthenticated path;
  mitigated by: succeeds only until the first admin is created (enforced by the
  `bootstrap_marker` sentinel insert), requires a one-time bootstrap secret,
  self-disables once the sentinel/first admin exists, `401`/`409` otherwise,
  rate-limited.
- [Redis-swappable seams not built yet] -> only MySQL impls ship; contracts are
  storage-agnostic so Redis is a later drop-in (D14); risk of a contract that assumes
  SQL is mitigated by reviewing each seam's method shapes.
- [ULID storage cost] -> CHAR(26) over BINARY(16) trades ~10 bytes/id for readability
  and convention fit; acceptable at these volumes (D15).
- [New dependencies] -> four MIT libraries, confined to Persistence (hashing/TOTP/ULID)
  and web (WebAuthn); none reach Core/Agent.
- [Tests need real MySQL] -> pure logic unit-tested without a DB; schema/store
  behaviour in integration tests skipped without `FREEBOARD_TEST_DB`; web endpoints
  with store doubles; CLI against an in-test API host.

## Migration Plan

- Ship `002_auth_core.sql` and `003_auth_mfa.sql` (forward-only, `IF NOT EXISTS`,
  binary collation, FK CASCADE, explicit `KEY (user_id)`). Apply with `system migrate`.
- Deploy order: `system migrate` -> set out-of-band secrets (Argon2 secret, token HMAC
  key set, TOTP encryption key, WebAuthn RP id + origins, trusted-proxy config, email
  sender, one-time bootstrap secret); the app fails fast at startup if password reset is
  enabled with no email sender or required keys are missing -> start the web app ->
  `user bootstrap` (creates the first admin via the `setup` endpoint) -> use the admin
  token for further `user` commands. Operators enroll MFA via the API after first login
  (which requires sudo-mode).
- The API namespace move is a breaking path change; document the new paths and
  (optionally) ship a 308 redirect from the old paths for one release.
- Rollback: additive schema; the namespace move and middleware change are in the web
  binary, so rolling back the binary restores prior behaviour. No down migrations.

## Open Questions

- Where shared auth records live (Core vs Persistence) - confirm whether touching Core
  for a `GlobalRole`/`UserAccount` record is acceptable or keep all in Persistence.
- Rate-limit thresholds/windows, and whether to ship the Redis impl now or later (D14
  ships MySQL only).
- Whether to ship the optional 308 redirect shim for the moved API paths (D16).
- Admin MFA-reset flow for a user who removed all factors and has no magic-link
  available (D7) - deferred; recovery codes cover the common case.

(Settled: `FREEBOARD_BOOTSTRAP_SECRET` is REQUIRED for the `setup` endpoint - the
secret AND a successful `bootstrap_marker` sentinel insert (i.e. no admin created yet)
are both needed; the sentinel PK collision makes the first-admin creation race-safe -
per D13/F-32.)
