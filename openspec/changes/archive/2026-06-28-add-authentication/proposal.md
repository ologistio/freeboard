## Why

The persistence and GitOps layers are in place, but every web endpoint is open:
the compliance read endpoints and the GitOps status endpoint answer any caller, and
the forward principle recorded in the persistence change ("real apply must be
authenticated") has no auth layer to build on. Freeboard needs a user identity and
session model so the web API can require a logged-in user before any future
authenticated write (real `apply`, mutating admin endpoints).

This change adds a clean, Freeboard-native authentication API with the capabilities
an operator expects: email + password login, opaque bearer session tokens, session
management, the password lifecycle (change, forgot, reset, forced-reset completion),
and multi-factor authentication (passkeys, TOTP, recovery codes, and an emailed
magic-link fallback). It offers auth capabilities equivalent to mature device-
management tools such as FleetDM, but the request/response shapes, paths, and field
names are designed for Freeboard, not copied from any other product.

## What Changes

- Namespace the whole web API under a single `/api/v1/freeboard/*` prefix (one
  `ApiRoutePrefix` constant). Move the existing compliance read
  endpoints and the GitOps status endpoint under it too, so the API is consistently
  namespaced. **BREAKING** for the existing `/api/standards`, `/api/controls`,
  `/api/scopes`, `/api/compliance/status`, and `/api/gitops/status` paths; the old
  paths are removed (a redirect shim is optional, noted in design).
- Add a user store: users persisted in MySQL with passwords hashed by Argon2id with
  a keyed secret (pepper via Argon2's native secret parameter), never plaintext.
  Password credentials live in a separate table from the user profile.
- Add opaque, server-side session tokens: a random secret returned as a bearer token
  on login, stored only as a keyed HMAC hash, validated per request, revocable on
  logout and session-delete. No JWT.
- Add the Freeboard auth REST surface under `/api/v1/freeboard/*` with clean,
  conventional REST shapes:
  - `POST /auth/login` (email + password) -> `{ user, token }`, a `202`
    MFA-required response, `401`, or `429`.
  - `POST /auth/logout`; `GET /auth/me` (the current user).
  - Password lifecycle: `POST /auth/password/change`, `POST /auth/password/forgot`,
    `POST /auth/password/reset`, and `POST /account/password` (a force-reset user
    setting a new password - the rename of the old unwieldy name).
  - Session management: `GET`/`DELETE /auth/sessions/{id}` and
    `GET`/`DELETE /users/{id}/sessions`.
- Add MFA (two-step challenge) supporting passkeys (WebAuthn/FIDO2), TOTP, recovery
  codes, AND an emailed magic-link FALLBACK used when the account has no stronger
  factor enrolled and an email sender is configured. MFA is optional per user;
  challenge state is stored as short-lived hashed tokens in MySQL with a max-attempts
  cap; the MFA challenge token is body-only and never a session bearer.
- Add a reusable sudo-mode (step-up auth): a `RequireSudoMode` policy any endpoint
  can opt into, enforced by a fresh-MFA/reauth timestamp on the session; entered via
  a reauth endpoint. The mandatory reauth for MFA-state changes is one consumer of
  this general mechanism.
- Add login and MFA-verify rate limiting behind a storage-agnostic
  `IAuthRateLimitStore` (atomic check-and-increment, reset, retention) so a Redis
  implementation can replace the default MySQL one for multi-server scale. SEPARATE
  per-account and per-IP buckets, returning `429` with `retry-after`, not an
  enumeration oracle, with trusted-proxy client-IP extraction.
- Add a bearer-token authentication handler so protected endpoints require a valid,
  unexpired, unrevoked session tied to an enabled user. A force-password-reset user
  gets a limited session that may only call `me`, `logout`, and the account-password
  endpoint. Existing public read endpoints stay public this increment.
- Exempt the specific auth ENDPOINTS (by endpoint marker, not the whole prefix) from
  `GitOpsReadOnlyMiddleware` so login/logout/reset/MFA work when the instance is
  GitOps read-only, while non-auth writes still return `409`.
- Add a CLI `user` command group that calls the HTTP API (authenticated with an
  admin token), NOT the database. The CLI never touches the user store directly.
  First-admin bootstrap is solved by an API bootstrap endpoint that succeeds only
  until the first admin is created - enforced by a `bootstrap_marker` sentinel insert
  (one successful sentinel insert is the first-and-only bootstrap) - and is gated by a
  one-time bootstrap secret, then self-disables. `system migrate` remains the only
  direct-DB CLI path.
- Add schema migrations `002_auth_core.sql` and `003_auth_mfa.sql` (users, password
  credentials, sessions, password-reset tokens, auth rate limits, MFA challenges,
  WebAuthn credentials, TOTP secrets, recovery codes). Apply via `system migrate`.
- Use ULIDs (lexically/time sortable) for all generated ids, stored as Crockford
  base32 `CHAR(26)` with binary collation, consistent with the existing string-id
  schema convention.
- Update `CLAUDE.md` to list `Freeboard.Persistence` in the project table and graph.

## Capabilities

### New Capabilities

- `user-accounts`: the MySQL-backed user store - schema and migrations for users and
  the separate password-credentials table, Argon2id hashing with a keyed secret,
  account fields, the API bootstrap path for the first admin, and the user
  read/write abstractions in `Freeboard.Persistence`.
- `password-auth`: the email + password login flow and password lifecycle - login,
  password change/forgot/reset, and the force-reset account-password completion.
- `session-tokens`: opaque server-side session tokens - creation on login,
  keyed-HMAC-at-rest storage, per-request bearer validation, expiry, revoke on logout
  and session-delete, the bearer-auth handler, `me`, and session-management endpoints.
- `mfa`: multi-factor authentication - passkeys (WebAuthn/FIDO2), TOTP, recovery
  codes, the emailed magic-link fallback factor, the two-step login challenge backed
  by hashed DB challenge tokens, and sudo-mode/step-up enforcement.
- `auth-rate-limits`: login and MFA-verify throttling behind a storage-agnostic
  `IAuthRateLimitStore` (MySQL default, Redis-swappable), enumeration-safe `429` with
  `retry-after` and trusted-proxy client-IP extraction.
- `user-admin`: the admin user-management HTTP endpoints (create, list, get, enable,
  disable, reset-password) behind the admin authorization policy, with session
  revocation on disable/reset - the contract the CLI consumes.
- `user-cli`: the `user` CLI command group that administers users through the
  authenticated HTTP API (not the DB).

### Modified Capabilities

- `compliance-web-read`: the compliance read endpoints and the compliance status
  endpoint move under `/api/v1/freeboard/*`. This changes their documented paths.
- `gitops-readonly-ui`: the GitOps status endpoint moves under `/api/v1/freeboard/*`,
  and the read-only middleware exempts the specific auth endpoints (so login etc.
  work in read-only mode) while still blocking non-auth writes.

## Impact

- Code:
  - `Freeboard.Persistence` (MIT): new `Auth/*` namespaces (user, password-
    credential, session, reset-token, MFA, challenge, and rate-limit stores), SQL,
    migrations `002`/`003`, Argon2id hashing with a keyed secret, keyed token hashing,
    TOTP secret encryption, ULID id generation. Reuses `IDbConnectionFactory`, the
    migration runner, and the embedded-migration mechanism.
  - `Freeboard` (web): the auth endpoint group under `/api/v1/freeboard/*`, the
    bearer-auth handler, sudo-mode policy, MFA endpoints (WebAuthn via fido2-net-lib),
    magic-link via the email sender seam, rate-limit enforcement, trusted-proxy
    config, the bootstrap endpoint, the scoped read-only-middleware exemption, the
    moved compliance/GitOps endpoints, and DI. Adds fido2-net-lib (web-only).
  - `Freeboard.CLI`: a `user` command group that calls the HTTP API via an API
    client; no persistence/user-store reference. `system migrate` unchanged (direct
    DB).
  - `Freeboard.Core`: unchanged except optionally a pure shared data record (role
    enum / user record); no crypto/DB/network dependency.
  - Test projects: persistence integration tests; web tests for auth/MFA/session/
    sudo/bootstrap and the moved routes; CLI tests against a test API; architecture
    tests.
  - `CLAUDE.md`: add `Freeboard.Persistence` to the project table and graph.
- Dependencies (all MIT): `Konscious.Security.Cryptography.Argon2` (Argon2id, keyed
  secret), `Otp.NET` (TOTP), `Fido2`/`Fido2.AspNet` (WebAuthn), and `Ulid` (Cysharp,
  ULID ids). Hashing/TOTP/ULID in `Freeboard.Persistence`; WebAuthn web-only. None
  reach `Freeboard.Core` or `Freeboard.Agent`.
- Reference graph: unchanged projects. The CLI no longer needs the persistence user
  store for `user` commands (it uses an HTTP client); it keeps the persistence
  reference only for `system migrate`. Core/Agent/Enterprise gain nothing; CLI/Agent
  never reference Enterprise.
- Licensing: MIT. Authentication is foundational community plumbing, not a paid
  feature. SSO (the usual paid-tier auth feature) is descoped; if it later lands it
  is the natural EE carve-out.
- Secrets/required config: the session-token HMAC key set, the password Argon2 secret
  (pepper), the TOTP secret-encryption key, the WebAuthn RP id + allowed origins,
  trusted-proxy config, the email sender config (for magic-link and forgot-password),
  and the one-time bootstrap secret - env/user-secrets/config provider only, never
  committed and never in the GitOps YAML. The CLI no longer needs the pepper (only the
  web app hashes).

## Non-goals

In scope: user accounts, password login + lifecycle, opaque session tokens, bearer
auth, session management, MFA (passkeys, TOTP, recovery codes, magic-link fallback),
sudo-mode/step-up, login rate limiting, the API bootstrap path, the namespaced API
move, and CLI user administration via the API. NOT in this change:

- No SSO (no SAML or OIDC IdP integration). A future extension and the likely EE
  carve-out.
- No real, reconciling, authenticated `apply`. `apply` stays dry-run. This change
  builds the auth layer a future authenticated `apply` will require.
- No requirement that existing public read endpoints become authenticated this
  increment. The bearer-auth handler is wired and ready; flipping current public
  endpoints to require auth is a separate change.
- No web UI login pages or rendered views. JSON endpoints only.
- No full SMTP stack owned by this change: the `IAuthEmailSender` seam and the
  magic-link/forgot-password flows are built; a concrete transport is configuration
  the operator supplies. When no sender is configured, password reset is disabled
  (forgot-password still returns a uniform response) and magic-link MFA is not offered.
- No fine-grained RBAC beyond a coarse global role.
- No personal access tokens distinct from session tokens; session-token HMAC key
  rotation IS supported.
- No multi-tenant user partitioning and no agent involvement. The Agent does not
  change.
