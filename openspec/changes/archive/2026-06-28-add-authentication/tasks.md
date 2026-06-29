## 1. ULID, schema, hashing, token hashing, and user store

Commit: `feat(persistence): add user store with argon2id keyed-secret hashing`

- [x] 1.1 Add the `Ulid` (Cysharp, MIT, runs on net10) package to `Freeboard.Persistence` and
  an `IUlidFactory` seam (new ULID + parse), so id generation is testable (D15). All
  generated auth ids are ULIDs stored as `CHAR(26) COLLATE utf8mb4_bin`.
- [x] 1.2 Add migration `Migrations/002_auth_core.sql` (embedded resource), all
  `CREATE TABLE IF NOT EXISTS`, FK ON DELETE CASCADE, explicit `KEY (user_id)` on
  user-owned tables, ids `CHAR(26) COLLATE utf8mb4_bin`, hash columns `BINARY(32)`.
  Columns must match design D3/D8/D11 exactly:
  - `users` (`id` CHAR(26) PK, `email` VARCHAR(190) NOT NULL, `email_normalized`
    VARCHAR(190) NOT NULL UNIQUE binary, `name` VARCHAR(255) NOT NULL, `global_role`
    VARCHAR(32) NOT NULL, `enabled` TINYINT(1) NOT NULL DEFAULT 1,
    `force_password_reset` TINYINT(1) NOT NULL DEFAULT 0, `mfa_enabled` TINYINT(1) NOT
    NULL DEFAULT 0, `created_at` DATETIME(6) NOT NULL, `updated_at` DATETIME(6) NOT
    NULL).
  - `user_password_credentials` (`user_id` CHAR(26) PK/FK, `password_hash`
    VARCHAR(255) NOT NULL, `secret_version` INT NOT NULL, `updated_at` DATETIME(6) NOT
    NULL).
  - `sessions` (`id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK, `token_hash`
    BINARY(32) NOT NULL UNIQUE, `token_key_version` INT NOT NULL, `auth_state` TINYINT
    NOT NULL, `sudo_at` DATETIME(6) NULL, `created_at` DATETIME(6) NOT NULL,
    `expires_at` DATETIME(6) NOT NULL, `last_seen_at` DATETIME(6) NULL, KEY (user_id)).
  - `password_reset_tokens` (`id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK,
    `token_hash` BINARY(32) NOT NULL UNIQUE, `token_key_version` INT NOT NULL,
    `expires_at` DATETIME(6) NOT NULL, `used_at` DATETIME(6) NULL, `created_at`
    DATETIME(6) NOT NULL, KEY (user_id)).
  - `auth_rate_limits` (`bucket_kind` VARCHAR(16) NOT NULL, `bucket_key` VARCHAR(190)
    NOT NULL utf8mb4_bin, `attempt_count` INT NOT NULL DEFAULT 0, `window_started_at`
    DATETIME(6) NOT NULL, `locked_until` DATETIME(6) NULL, PRIMARY KEY (bucket_kind,
    bucket_key)).
  - `mfa_login_challenges` (`id` CHAR(26) PK, `challenge_token_hash` BINARY(32) NOT
    NULL UNIQUE, `token_key_version` INT NOT NULL, `user_id` CHAR(26) NOT NULL FK,
    `factors` VARCHAR(64) NOT NULL, `webauthn_options` JSON NULL, `magic_link_token_hash`
    BINARY(32) NULL, `magic_link_token_key_version` INT NULL (F-34), `magic_link_expires_at`
    DATETIME(6) NULL, `magic_link_sends` INT NOT NULL DEFAULT 0 (F-41), `expires_at`
    DATETIME(6) NOT NULL, `consumed_at` DATETIME(6) NULL, `attempts` INT NOT NULL
    DEFAULT 0, `created_at` DATETIME(6) NOT NULL, KEY (user_id)).
  - `bootstrap_marker` (`id` TINYINT NOT NULL PRIMARY KEY, `created_at` DATETIME(6) NOT
    NULL) - the single-row sentinel that guards the first-admin race (F-32).

  Do NOT create `schema_migrations`.
- [x] 1.3 Add `IPasswordHasher` (Hash, Verify, NeedsRehash) using Argon2id via
  `Konscious.Security.Cryptography.Argon2` (pinned; pure-managed, runs on net10) with
  the REQUIRED out-of-band secret mixed in as the Argon2 KEYED secret parameter
  (`KnownSecret`), not concatenation; versioned secret for rotation; self-describing
  PHC output (D1). Provide a fixed decoy hash for constant-work verification (F-10).
- [x] 1.4 Add `ITokenHasher` over the versioned key set `FREEBOARD_AUTH_TOKEN_KEYS`,
  HMAC-SHA256. Two modes (D2): prefix-bearing tokens (EXACTLY session, password-reset,
  and the MFA-challenge token) use `v<keyId>.<secret>` and parse the key id from the
  token; prefixless tokens (recovery codes AND the magic-link token) carry no prefix and
  use an explicit stored key-version column (`mfa_recovery_codes.token_key_version` and
  `mfa_login_challenges.magic_link_token_key_version` respectively). Add the CSPRNG
  secret generator.
- [x] 1.5 Add `IUserStore` + `MySqlUserStore` and `IPasswordCredentialStore` +
  `MySqlPasswordCredentialStore` (Dapper): create, get-by-id, get-by-normalized-email,
  update hash + secret_version, set enabled, set/clear force_password_reset, set
  mfa_enabled, count (for bootstrap). Profile reads never carry the hash.

## 2. Sessions, bearer auth, sudo-mode, force-reset gating, rate limiting

Commit: `feat(persistence): add session and rate-limit stores`

- [x] 2.1 Add `ISessionStore` + `MySqlSessionStore` (storage-agnostic contract, D14):
  create, find-by-token-hash, get-by-id, list-by-user, delete (single + all-for-user),
  set `sudo_at`, upgrade `auth_state` IN PLACE (same row, token unchanged), prune
  expired. Expiry from config, default 24h.
- [x] 2.2 Add `IAuthRateLimitStore` + `MySqlAuthRateLimitStore` (storage-agnostic, no
  SQL in the contract, D11/D14): atomic check-and-increment returning state/retry-after,
  reset(account key), prune(retention). SEPARATE per-account and per-IP buckets;
  account-only reset on success; lock unknown emails (enumeration-safe).

Commit: `feat(web): add bearer auth handler, sudo-mode, and rate limiting`

- [x] 2.3 Add ONE aggregate `AddAuth(connectionString)` DI extension registering the
  connection factory ONCE plus all auth stores, `IPasswordHasher`, `ITokenHasher`,
  `ISecretProtector`, `IUlidFactory`, `IAuthEmailSender` (D8).
  (Crypto options bound from `IConfiguration` with required validation. The `IAuthEmailSender`
  seam is defined here but NOT registered concrete - transport is later. The
  password-reset store is a group-3 task and is skipped until it exists.)
- [x] 2.4 Add a bearer auth handler in `Freeboard` (web): parse key id from the
  `v<keyId>.` prefix, HMAC via `ITokenHasher`, look up by `token_hash`, integrity-assert
  the stored `token_key_version` == parsed key id (mismatch -> 401); reject
  missing/unknown/expired/revoked/disabled-user and any MFA challenge token with `401`;
  reject malformed/unknown-key tokens with a uniform `401` and NO DB lookup. For an
  `auth_state` = limited (force-reset) session allow only me/logout/account-password,
  else `403`.
- [x] 2.5 Add the sudo-mode mechanism (D9): `POST /api/v1/freeboard/auth/sudo`
  re-confirms ANY of the user's currently-usable factors - the SAME set login MFA would
  accept (passkey, TOTP, recovery code, or magic-link fallback when applicable), or a
  password for non-MFA users (F-35) - and stamps `sessions.sudo_at`; a `RequireSudoMode`
  policy/filter checks `sudo_at` within the TTL (default 5 min) and returns `403`
  otherwise. Rate-limit the sudo endpoint (F-40). This lets a magic-link-only user step
  up via magic-link to enroll a strong factor.
  (Policy + endpoint DONE: `RequireSudoModeRequirement`/`Handler`/`RequireSudoMode()` enforce
  the TTL; `SudoEndpoints` adds `POST /auth/sudo` (password/totp/recovery/passkey/magic-link),
  `POST /auth/sudo/passkey/options`, and `POST /auth/sudo/magic-link/send`, rate-limited.
  A magic-link-only user stepping up via magic-link to enroll is covered by a web test.)
- [x] 2.6 Add trusted-proxy forwarded-headers config (KnownProxies/KnownNetworks) so
  the client IP for rate limiting and WebAuthn origin is trustworthy.
- [x] 2.7 Add the rate-limit enforcement helper (`AuthRateLimiter`) the login/`/auth/mfa/*`/
  sudo endpoints will call: check both buckets before expensive work, `429` + `retry-after`,
  account-only reset on success. (Endpoints wire it in the next sub-round.)

## 3. Freeboard-native auth and session endpoints + API namespace move

Commit: `feat(web): namespace the API under /api/v1/freeboard and add auth endpoints`

- [x] 3.1 Add a single `ApiRoutePrefix = "/api/v1/freeboard"` constant and an
  `AuthEndpoint` metadata marker. Move the EXISTING endpoints under it (D16):
  `/api/standards` -> `/api/v1/freeboard/standards`, `/controls`, `/scopes`,
  `/compliance/status`, and `/api/gitops/status` ->
  `/api/v1/freeboard/gitops/status`. Optionally add 308 redirects from the old paths
  (not required). Add response/error helpers (the Freeboard user object;
  `422` validation body).
- [x] 3.2 `POST /auth/login` (`{email,password}`): rate-limit -> `429`; ALWAYS run a
  password verify (decoy for unknown/disabled, F-10); disabled/wrong -> generic `401`;
  success no MFA -> `200 {user,token}` (auth_state by force_password_reset); success
  with MFA -> `202` (group 5). Normalize email; upgrade hash if NeedsRehash.
- [x] 3.3 `GET /auth/me` (bearer) and `POST /auth/logout` (bearer; revoke current
  session).
- [x] 3.4 `POST /auth/password/change` (bearer, `{old_password,new_password}`, `422`
  on mismatch, revoke other sessions; NOT sudo-gated - old_password is the proof).
- [x] 3.5 `POST /auth/password/forgot` (`{email}`, ALWAYS uniform `200`; issue a
  keyed-hashed reset token and send via `IAuthEmailSender`; STARTUP fail-fast if reset
  is enabled with no sender) and `POST /auth/password/reset` (`{token,new_password}`,
  revoke sessions).
- [x] 3.6 `POST /account/password` (bearer incl. limited session, `{new_password}`,
  clear force_password_reset, upgrade the caller's session IN PLACE to full). This
  replaces the old `perform_required_password_reset` name (D10).
- [x] 3.7 Session management: `GET`/`DELETE /auth/sessions/{id}` (ULID param),
  `GET`/`DELETE /users/{id}/sessions`. Non-admin may act only on OWN sessions;
  non-owned target -> `404` (not `403`); admin may act cross-user.
- [x] 3.8 Add `IPasswordResetStore` + `MySqlPasswordResetStore`. Tag all auth
  endpoints with the `AuthEndpoint` marker (for group 6).
  (Store registered in `AddAuth`; all auth/admin/setup endpoints carry the
  `AuthEndpoint` marker via `MarkAuthEndpoint()`.)
- [x] 3.9 Add the admin user-management endpoints (the `user-admin` capability, F-36),
  all under `/api/v1/freeboard/` behind an admin authorization policy (non-admin ->
  `403`; ULID `{id}` params; `422` on validation): `POST /users` (create
  `{email,name,global_role}`; generate a random ONE-TIME temp password, store its
  Argon2id hash, set `force_password_reset`, return `{user, temporary_password}` ONCE -
  no email; `422` on duplicate email), `GET /users` (no password), `GET /users/{id}`
  (`404` if absent), `POST /users/{id}/disable` (revokes that user's sessions),
  `POST /users/{id}/enable`, `POST /users/{id}/reset-password` (generate a new ONE-TIME
  temp password, store its hash, set `force_password_reset`, revoke sessions, return
  `{temporary_password}` once - no email). Mark them with `AuthEndpoint` (F-36).
- [x] 3.10 Add `POST /api/v1/freeboard/setup` (the first-admin bootstrap, F-32): REQUIRE
  the one-time `FREEBOARD_BOOTSTRAP_SECRET` (wrong/absent -> `401`, never opening the
  transaction). Guard the race with the `bootstrap_marker` sentinel table: inside ONE
  transaction FIRST `INSERT INTO bootstrap_marker (id) VALUES (1)` and rely on the
  primary-key collision (the loser's duplicate-key error -> `409`); only the winner
  inserts the admin user and commits in the same transaction. (Acceptable equivalents:
  `GET_LOCK('freeboard_bootstrap', ...)` or `SELECT ... FOR UPDATE` on the sentinel
  row.) Rate-limited. Mark with `AuthEndpoint`. Return the admin + an admin token.

## 4. MFA schema and factor stores

Commit: `feat(persistence): add mfa schema, totp, challenge, and recovery stores`

- [x] 4.1 Add migration `Migrations/003_auth_mfa.sql` (columns match design D4/D5),
  `CREATE TABLE IF NOT EXISTS`, ids `CHAR(26) COLLATE utf8mb4_bin`, FK CASCADE,
  explicit `KEY (user_id)`:
  - `webauthn_credentials` (`id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK,
    `credential_id` VARBINARY(255) NOT NULL UNIQUE, `public_key` VARBINARY(1024) NOT
    NULL, `sign_count` BIGINT UNSIGNED NOT NULL, `user_handle` VARBINARY(64) NOT NULL,
    `aaguid` CHAR(36) NULL, `transports` VARCHAR(255) NULL, `cred_type` VARCHAR(32)
    NULL, `is_backup_eligible` TINYINT(1) NULL, `is_backed_up` TINYINT(1) NULL,
    `nickname` VARCHAR(190) NULL, `created_at` DATETIME(6) NOT NULL, `last_used_at`
    DATETIME(6) NULL, KEY (user_id)).
  - `totp_credentials` (`user_id` CHAR(26) PK/FK, `secret_ciphertext` VARBINARY(255)
    NOT NULL, `secret_nonce` VARBINARY(12) NOT NULL, `secret_tag` VARBINARY(16) NOT
    NULL, `key_version` INT NOT NULL, `confirmed_at` DATETIME(6) NULL, `last_time_step`
    BIGINT NULL, `created_at` DATETIME(6) NOT NULL).
  - `mfa_recovery_codes` (`id` CHAR(26) PK, `user_id` CHAR(26) NOT NULL FK, `code_hash`
    BINARY(32) NOT NULL, `token_key_version` INT NOT NULL, `used_at` DATETIME(6) NULL,
    `created_at` DATETIME(6) NOT NULL, KEY (user_id)).
- [x] 4.2 Add `ISecretProtector` (AES-256-GCM, REQUIRED out-of-band key, key version)
  for TOTP secrets; fail loudly if no key is configured.
- [x] 4.3 TOTP via `Otp.NET` (pinned): generate secret, provisioning URI, verify with
  a +/-1 window, atomic `last_time_step` advance. `ITotpStore` + `MySqlTotpStore`.
- [x] 4.4 Recovery codes (10, single-use, keyed-HMAC at rest with stored
  `token_key_version`, atomic consume, regenerate replaces set; ALWAYS a valid factor;
  verify via stored key version so they survive key rotation): `IRecoveryCodeStore` +
  `MySqlRecoveryCodeStore`.
- [x] 4.5 `IWebAuthnCredentialStore` + `MySqlWebAuthnCredentialStore` (store/load,
  update sign_count with the synced-passkey rule, list/remove by user).
- [x] 4.6 `IMfaChallengeStore` + `MySqlMfaChallengeStore` (storage-agnostic contract,
  D14): create with keyed-hashed challenge token + factors + optional webauthn options;
  find by hash; atomic increment of `attempts`; atomic consume; auto-consume after 5
  failed attempts; set the magic-link token as a keyed HMAC plus its OWN
  `magic_link_token_key_version` and verify it with the key named by that stored column
  (F-34); atomically increment `magic_link_sends` and reject past the re-send cap (F-41).

## 5. MFA login flow, factors, and magic-link fallback

Commit: `feat(web): add passkey, totp, recovery, and magic-link mfa`

- [x] 5.1 Add `Fido2`/`Fido2.AspNet` (pinned) to `Freeboard` (web) and a WebAuthn
  ceremony service. RP id + allowed origins are EXPLICIT REQUIRED config outside dev;
  honor forwarded host/proto only via trusted proxy; reject mismatched origin/RP-id on
  registration AND assertion; require user verification. Correlate options to the
  challenge row.
- [x] 5.2 MFA-required login branch (from 3.2): mint a keyed-hashed challenge token,
  persist the row, return `202 {mfa_required,mfa_token,factors}`. The `mfa_token` is
  body-only and never a session bearer.
- [x] 5.3 Verify endpoints under `/auth/mfa/`: `totp`, `passkey/options` + `passkey`,
  `recovery`. Read `mfa_token` from the body; rate-limited; consume a valid challenge,
  enforce the 5-attempt cap, return `200 {user,token}` on success. Enforce the
  WebAuthn sign-counter rule (accept 0; reject regression only when both positive).
- [x] 5.4 Magic-link fallback (D7): when `mfa_enabled` is true but the user has no
  passkey and no TOTP AND a sender is configured, include `magic_link` in the factors
  list (every account has an email - `users.email` is NOT NULL - so the only gate is
  the sender, F-38). `POST /auth/mfa/magic-link/send` emails a single-use short-TTL
  PREFIXLESS token (no `v<keyId>.` prefix, like recovery codes) stored as a keyed HMAC
  plus its own `magic_link_token_key_version` on the challenge row; `send` is
  rate-limited and re-send-capped (F-41). `POST /auth/mfa/magic-link/verify` HMACs the
  emailed token with the key named by the STORED `magic_link_token_key_version` (F-34,
  not a parsed prefix) and completes the challenge. If no sender, do not offer
  `magic_link` and return a clear error from send.
- [x] 5.5 Enrollment endpoints (bearer + RequireSudoMode, D9) under `/auth/mfa/`:
  `GET /auth/mfa` (status, no sudo); TOTP `enroll`/`activate` + delete; passkey
  `options`/register + per-credential delete; `recovery/regenerate`. Maintain
  `mfa_enabled`; return recovery codes once on the first factor.
- [x] 5.6 Add the NET-NEW `IAuthEmailSender` interface (no prior notifier to
  generalize, F-39) with a method per email kind (password reset, magic link). Concrete
  transport is operator config; ship the seam only.

## 6. GitOps read-only middleware: namespace move + scoped auth exemption

Commit: `fix(web): run read-only middleware after routing and exempt auth endpoints`

- [x] 6.1 ADD an explicit `app.UseRouting()` BEFORE
  `app.UseMiddleware<GitOpsReadOnlyMiddleware>()` in `Program.cs` (Program.cs has none
  today), leaving endpoint mappings after it so `context.GetEndpoint()` is populated.
  Narrow the middleware to skip its `409` ONLY when
  `context.GetEndpoint()?.Metadata.GetMetadata<AuthEndpoint>()` is present, NOT for the
  whole `/api/v1/freeboard/` prefix. Non-auth mutating routes (including non-auth
  routes under the prefix) still get `409`. Update the GitOps status path to
  `/api/v1/freeboard/gitops/status`.

## 7. CLI user administration via the HTTP API

Commit: `feat(cli): add user command group calling the http api`

- [x] 7.1 Add an `IFreeboardApiClient` in `Freeboard.CLI` (HttpClient against
  `/api/v1/freeboard/*`); base URL via `--api-url`/`FREEBOARD_API_URL`, admin token via
  `--token`/`FREEBOARD_ADMIN_TOKEN`. No persistence/user-store reference; no DB. The
  `user` group references only the HTTP client.
- [x] 7.2 `user create/list/disable/enable/reset-password` map one-to-one to the
  `user-admin` endpoints (3.9): `POST /users`, `GET /users`, `POST /users/{id}/disable`,
  `POST /users/{id}/enable`, `POST /users/{id}/reset-password`. `create` and
  `reset-password` PRINT the returned one-time temporary password exactly once (F-36).
  Exit `0` success, `1` validation (surface the API `422`), `3` operational/HTTP failure
  (incl. `401`/`403`/`5xx`/connection). No Argon2, no pepper, no DB.
- [x] 7.3 `user bootstrap` calls `POST /api/v1/freeboard/setup` with the one-time
  bootstrap secret (`--bootstrap-secret`/env) and the new admin's details; prints the
  returned admin token; `3` if a user already exists (`409`) or the secret is wrong.
- [x] 7.4 Confirm the CLI keeps the `Freeboard.Persistence` reference ONLY for
  `system migrate`; remove any direct user-store usage from the CLI.
- [x] 7.5 Add a test seam (inject a fake `IFreeboardApiClient`) so CLI tests run
  without a live API.

## 8. Tests

Commit: `test(persistence): cover hashing, totp, tokens, ulid, rate limits, schema`

- [~] 8.1 Unit tests (no MySQL): Argon2id keyed-secret round-trip, distinct salts,
  verify failure, NeedsRehash; constant-work decoy verify for unknown/disabled (F-10);
  keyed token HMAC + prefix key-id selection/rotation; recovery-code HMAC via STORED
  key version verifies after rotation; TOTP compute/verify with skew + replay; AES-GCM
  round-trip; WebAuthn sign-counter rule (accept 0, reject positive regression); email
  normalization; ULID sortability.
  (Group 1 parts done: Argon2id round-trip/distinct salts/verify failure/NeedsRehash,
  constant-work decoy verify, keyed token HMAC + prefix key-id selection/rotation +
  prefixless stored-version verify, email normalization, ULID sortability.
  Group 4 no-DB parts done: AES-GCM round-trip + tamper failure + rotation, the WebAuthn
  sign-counter pure rule, recovery-code HMAC verify-after-rotation via the stored key
  version, and TOTP compute/verify with the +/-1 window. The DB round-trips - TOTP replay,
  recovery-code consume, challenge consume/cap, magic-link set/verify - remain for 8.2.)
- [x] 8.2 Integration tests (real MySQL, skipped when `FREEBOARD_TEST_DB` absent):
  apply `002`/`003`, assert tables/collation/FKs/`KEY (user_id)` and that
  `token_key_version` is NOT NULL on `sessions`, `password_reset_tokens`,
  `mfa_login_challenges`, `mfa_recovery_codes`; CHAR(26) ULID ids; user create +
  normalized-email uniqueness + separate credentials + count(0) for bootstrap; session
  create/find/expire(24h)/revoke/cascade and `sudo_at` set/read; reset-token
  single-use; SEPARATE account/IP rate-limit buckets with atomic increment,
  enumeration-safe lock of unknown emails, account-only reset, prune, persistence
  across restart; TOTP secret encrypted at rest + atomic replay; challenge consume +
  5-attempt auto-consume; magic-link token set/verify single-use AND verify after a key
  rotation via the stored magic-link key version (F-34) AND re-send cap (F-41); the
  atomic bootstrap insert creates exactly one admin under concurrent calls (F-32);
  webauthn store/counter update.

Commit: `test(web): cover login, sessions, password, mfa, sudo, bootstrap, routes`

- [x] 8.3 Web tests with store doubles: login `200`/`401`/`202`/`429`; unknown and
  disabled logins both invoke the verifier; `me`/login user object fields; logout
  revocation; `password/change` `422` + not sudo-gated; `password/forgot` identical for
  known/unknown; session IDOR `404` for non-owned, admin cross-user works; force-reset
  limited session blocked then same token works after `account/password`;
  malformed/unknown-key bearer -> uniform `401` no lookup; MFA two-step (TOTP,
  recovery, magic-link fallback) yields a session; challenge token rejected as a
  bearer; 5-attempt invalidation; sudo-mode blocks MFA-state changes without a recent
  `sudo_at` and allows them after `/auth/sudo`, INCLUDING a magic-link-only user
  stepping up via magic-link to enroll a strong factor (F-35); admin user-management
  endpoints (`POST /users` returns the temp password ONCE and sets force_password_reset,
  list has no password, get `404`, disable revokes sessions, enable, reset-password
  returns a fresh temp password once and revokes sessions) with non-admin `403` and
  duplicate-email `422` (F-36); bootstrap creates first admin / self-disables / rejects
  wrong secret (`401`).
  (DONE for the group-3 endpoints: login `200`/`401`/`202`/`429` + the I-8 unknown/disabled
  decoy-verify proof; `me`/`logout`; `password/change` `422` + not sudo-gated;
  `password/forgot` uniform `200`; `password/reset` single-use; session IDOR `404` +
  admin cross-user; force-reset limited blocked then upgraded via `account/password`;
  malformed/unknown-key bearer `401`; admin user-management; bootstrap create/self-disable/
  wrong-secret. Group-5 MFA parts now also DONE: TOTP + recovery + magic-link two-step yield a
  full session; the 202 carries `mfa_token` + factors; the challenge token is rejected as a
  bearer; the 5-attempt cap invalidates the challenge; magic-link is offered only with no
  passkey/TOTP + a sender, and `send` is re-send-capped; sudo-mode blocks an MFA-state change
  without a recent `sudo_at` and allows it after `/auth/sudo`, including a magic-link-only user
  stepping up via magic-link to enroll a strong factor.)
- [x] 8.4 Route-move + read-only tests: the moved endpoints answer under
  `/api/v1/freeboard/*` (standards/controls/scopes/compliance status/gitops status);
  in read-only mode a marked auth endpoint (`POST /auth/login`) is NOT `409`, a
  non-auth mutating route under the prefix STILL `409`, GETs unaffected.
- [~] 8.5 WebAuthn round-trip + negative tests: enroll/assert persists and updates the
  counter, accepts a synced passkey (counter 0), and REJECTS wrong-origin registration
  and wrong-origin/RP-id assertion.
  (DONE without a real authenticator: the synced-passkey sign-counter rule is unit-tested in
  the persistence layer (accept 0, reject positive regression); `WebAuthnCeremonyTests` covers
  registration/assertion option generation and rejection of malformed/unverifiable responses
  (the same code path the library uses to reject a mismatched origin / RP-id, surfaced as
  `WebAuthnCeremonyException`). DEFERRED: a full happy-path enroll+assert round-trip and an
  explicit wrong-origin success-shaped response need a real authenticator or a recorded
  WebAuthn fixture - an integration/e2e harness, out of scope for store-double tests.)

Commit: `test(cli): cover user commands against a fake api`

- [x] 8.6 `user` commands via a fake `IFreeboardApiClient`: create maps to the API,
  prints the returned one-time temporary password once, and exits `0`; reset-password
  prints its returned temp password once; duplicate email `1`; missing token/url `3`;
  `401`/`403` -> `3`; bootstrap success prints the token, conflict -> `3`; CLI opens no
  DB connection for `user` commands.

Commit: `test(architecture): pin auth placement and core purity`

- [x] 8.7 Architecture tests: `Freeboard.Persistence` and `Freeboard.CLI` gain no
  `Freeboard.Enterprise` reference; `Freeboard.Agent` gains no auth/MFA/crypto/ULID
  dependency (no Fido2/Otp.NET/Argon2/Ulid); the no-network Core structural test
  passes; the CLI `user` commands do not depend on the persistence user store.

## 9. Docs and project metadata

Commit: `docs(auth): document auth api, mfa, sudo-mode, bootstrap, and the move`

- [x] 9.1 Document the Freeboard-native auth API under `/api/v1/freeboard/*` (login,
  sessions, password lifecycle, MFA incl. magic-link fallback, sudo-mode), the API
  namespace move (breaking; old paths removed; optional 308 shim), the first-admin
  bootstrap via `setup` + `user bootstrap`, the CLI-via-API model (no DB for `user`),
  and the REQUIRED out-of-band config (Argon2 secret, token HMAC key set, TOTP key,
  WebAuthn RP id + origins, trusted-proxy, email sender, one-time bootstrap secret) -
  env/user-secrets/config only.
- [x] 9.2 Update `CLAUDE.md`: add `Freeboard.Persistence` to the project table and
  reference graph.
- [x] 9.3 Run markdownlint on changed repo docs and fix issues.

## 10. Verification

Commit: gate before finishing.

- [x] 10.1 `dotnet build` succeeds.
- [x] 10.2 `dotnet test` green without MySQL (integration tests skip) and with MySQL
  when `FREEBOARD_TEST_DB` is set.
- [x] 10.3 Architecture and no-network structural tests pass.
- [x] 10.4 `openspec validate "add-authentication"` passes.
- [x] 10.5 Fixed I-26 (M): sudo magic-link resend cap was bypassable by concurrent first
  sends. Made find-or-create-the-active-sudo-magic-link-challenge + increment atomic via a
  `(user_id, sudo_dedupe_key)` unique key and `INSERT ... ON DUPLICATE KEY UPDATE`
  (migration `004`, `IMfaChallengeStore.FindOrCreateSudoMagicLinkAsync`). Added a web test
  and a skippable MySQL concurrency integration test.
