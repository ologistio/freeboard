## 1. Share the create / reset credential-handoff logic

<!-- refactor(auth): extract shared user-create and reset-password helpers -->

- [x] 1.1 Move ONLY the create-user-and-handoff and reset-password-and-handoff
  bodies out of the private handlers in `src/Freeboard/Auth/UserAdminEndpoints.cs`
  into shared methods on `AuthFlows`, with all current behavior intact (random
  temp password, store only the Argon2id hash, set `force_password_reset`, revoke
  sessions on reset). Each returns a result-record discriminated union mirroring
  `BootstrapResult` (`AuthFlows.cs` ~line 393), NOT a bare user + plaintext:
  - `CreateUserAsync` -> `CreateUserResult` with arms `Success(UserRow,
    string plaintext)` (temp-password path), `Invited(UserRow)` (email-invite
    path, no plaintext), `InviteSendFailed(UserRow)` (email-invite path: the row
    was created with `force_password_reset` and no password BEFORE the token mint +
    send, and EITHER the token mint or `SendInviteAsync` threw),
    `Invalid(IDictionary<string, string[]>
    errors)` (missing email/name, unknown role), `DuplicateEmail` (the pre-check
    AND the `MySqlException` duplicate-key catch both map here, so MySQL knowledge
    stays inside `AuthFlows`). `CreateUserAsync` takes a handoff selector; the JSON
    endpoint always requests the temp-password path so its contract is unchanged
    (`Invited` and `InviteSendFailed` are never returned to the API).
  - `ResetUserPasswordAsync` -> `ResetUserPasswordResult` with arms
    `Success(string plaintext)` and `UnknownUser` (for a stale/unknown id).
  - enable, disable, and list are NOT extracted: they are simple store flips/reads
    with no validation or plaintext, so both the endpoint and the page call the
    stores directly.
- [x] 1.2 Update `UserAdminEndpoints` to call the shared create/reset methods,
  mapping `Invalid`/`DuplicateEmail` to `ApiResponses.ValidationProblem`,
  `UnknownUser` to `Results.NotFound()`, and `Success` to the existing 201/200
  JSON; keep all JSON response shapes and status codes identical.
- [x] 1.3 Run `dotnet test --filter "FullyQualifiedName~UserAdminEndpointTests"`
  to confirm the API behavior is unchanged.

## 2. One-time temp-password display store

<!-- feat(web): add one-time temporary-password display store -->

- [x] 2.1 Add `src/Freeboard/Web/TempPasswordDisplayStore.cs` mirroring
  `RecoveryCodeDisplayStore` (IMemoryCache, short TTL, StrongBox +
  Interlocked.Exchange one-time claim, `Stash`, `StashAndRedirectTarget`,
  `Take`).
- [x] 2.2 Add one path-scoped transient cookie name to
  `src/Freeboard/Web/SessionCookie.cs` for the display nonce.
- [x] 2.3 Register `TempPasswordDisplayStore` as a singleton in `Program.cs`.
- [x] 2.4 Add `tests/Freeboard.Web.Tests/TempPasswordDisplayStoreTests.cs`
  (one-time take, concurrency-safe claim), mirroring
  `RecoveryCodeDisplayStoreTests`.

## 3. Email-invite credential handoff

<!-- feat(web): offer an email invite as a create-time credential handoff -->

- [x] 3.1 Add `AuthEmailService.SendInviteAsync(email, token)` in
  `src/Freeboard/Auth/AuthEmailService.cs`, building the link with the existing
  `BuildLink("/reset-password", token)` and a short invite body; never log the
  token (same rule the file states for reset/magic-link).
- [x] 3.2 In the shared `AuthFlows.CreateUserAsync`, add the email-invite branch:
  gate on `serviceProvider.GetService<AuthEmailService>() is not null` (the same
  check `ForgotPasswordAsync` uses); when invited, create the user with
  `force_password_reset` set and NO password credential (do not call
  `IPasswordCredentialStore.SetAsync`), mint a token via
  `IPasswordResetStore.CreateAsync(userId, now + 7 days)` (explicit 7-day expiry
  literal at the call site, NOT `WebAuthOptions.PasswordResetLifetime`; not new
  config), call `SendInviteAsync`, and return `Invited(user)`. The row is created
  BEFORE the token mint + send; wrap BOTH `IPasswordResetStore.CreateAsync` (token
  mint) and `SendInviteAsync` (email send) as one invite-provisioning step and map
  a throw from EITHER to `InviteSendFailed(user)` (the row already exists with
  `force_password_reset` and no usable credential). The invite gates ONLY on
  `AuthEmailService` presence and
  does NOT honor `Auth:PasswordResetEnabled` (deliberate divergence from
  `ForgotPasswordAsync`). When an invite is requested but no `AuthEmailService` is
  registered, fall back to the temp-password path (return `Success`) - the gate is
  enforced here, not only in markup.
- [x] 3.3 Add Unit tests in `tests/Freeboard.Web.Tests`: a fake `IEmailSender`
  captures the message; assert the invite branch creates the user with
  `force_password_reset` and no credential row, sends a `/reset-password?token=`
  link, generates no temp password, and that the token consumes once via
  `AuthFlows.ResetPasswordAsync` (single-use, clears force-reset) and fails on a
  second consume. Assert the unconfigured-email fallback returns `Success` with a
  temp password and sends nothing. Assert that an invite-provisioning failure in
  EITHER step returns `InviteSendFailed` with the user row present
  (`force_password_reset` set, no usable credential) and no temp password: one
  case where the email send throws (fake `IEmailSender` throws) and one where the
  token mint throws (fake `IPasswordResetStore.CreateAsync` throws).
- [x] 3.4 Add a Unit test for the `Auth:PasswordResetEnabled` divergence: with
  `AuthEmailService` registered (email configured) AND
  `Auth:PasswordResetEnabled=false`, an admin create-with-invite still returns
  `Invited`, mints an invite token, and the emitted `/reset-password?token=` link
  is consumable - the new user can set a password via `AuthFlows.ResetPasswordAsync`
  even though the public toggle is off. Guards against a regression that copies
  `ForgotPasswordAsync`'s gate (which DOES honor `PasswordResetEnabled`).

## 5. Admin pages

<!-- feat(web): add admin user-management pages -->

- [x] 5.1 Add `src/Freeboard/Pages/Admin/Users.cshtml` + `.cshtml.cs`: GET lists
  users; the page model holds an in-page admin-role guard (read
  `AuthClaims.Role`, return a bare `StatusCodeResult(403)` - NOT `Forbid()`, which
  the page scheme would redirect to `/account/sudo` - when not
  `GlobalRoles.Admin`) called at the top of every handler. POST handlers:
  create and reset-password call the shared `AuthFlows` methods
  (`CreateUserAsync`/`ResetUserPasswordAsync`); disable, enable, and list call
  the stores directly (`IUserStore`/`ISessionStore`), matching what the endpoint
  does. The create form renders a credential-handoff choice (temp password vs
  email invite); the invite radio is disabled with an explanation when no
  `AuthEmailService` is registered. Create passes the chosen handoff to
  `CreateUserAsync`; map `Success` to the display-page redirect via the
  temp-password store, `Invited` to an in-page invite-sent confirmation panel
  ("Invite sent to <email>, link expires in 7 days"; no redirect, no temp
  password), `InviteSendFailed` to an in-page error stating the user was created
  but the invite could not be provisioned and the recovery is the per-user reset-password
  action (re-create does NOT work - the duplicate-email pre-check blocks it; no
  temp-password auto-fallback, no re-send-invite action), and
  `Invalid`/`DuplicateEmail` to a re-rendered form with field errors. Reset maps
  `Success` to the display-page redirect.
  reset/enable/disable against a stale (unknown) id re-render the list with a
  not-found notice and make no change. Do NOT add the
  `LimitedSessionAllowed` marker to any admin page, so the limited-session guard
  funnels a force-reset session to `/account/complete-reset`.
- [x] 5.2 Add `src/Freeboard/Pages/Admin/UserCredential.cshtml` + `.cshtml.cs`:
  declare `@page "/admin/usercredential"` explicitly so the PascalCase file
  serves the lowercase route (matching `RecoveryCodes.cshtml`'s
  `@page "/account/mfa/recovery-codes"`), keeping the route literal, cookie
  `Path`, and redirect target one source of truth. GET reads the nonce, `Take`s
  the temp password, clears the cookie, sets
  `Cache-Control: no-store` and `Pragma: no-cache`, renders the password once;
  applies the same in-page admin guard (the admin check runs BEFORE `Take`, so a
  non-admin cannot consume the nonce).
- [x] 5.3 In `Program.cs`: add `AuthorizeFolder("/Admin",
  PageChallengeScheme.PolicyName)` and add the single mutating page `"/Admin/Users"`
  to the `AuthEndpoint` page-metadata loop so GitOps read-only mode exempts it. Do
  NOT add `"/Admin/UserCredential"` (GET-only).
- [x] 5.4 Use the shared `_Layout` and `_ErrorSummary` partials; no client-side
  reactivity. Give the users list page a wide content slot (an `admin-wide` class
  on its container plus a `max-width` rule in `css/auth.css`) so the multi-column
  table is not cramped in the narrow `main.auth` layout; `UserCredential` keeps
  the default narrow width.

## 6. Unit tests for the pages

<!-- test(web): cover admin page auth, antiforgery, and one-time handoff -->

- [x] 6.1 Add `tests/Freeboard.Web.Tests/AdminUserPagesTests.cs` (no Category
  trait; runs in the default Unit tier).
- [x] 6.2 Cover: unauthenticated GET redirects to `/login`; a force-reset-limited
  session GET is redirected to `/account/complete-reset`; authenticated non-admin
  GET and each POST return 403 with no mutation (the non-admin POST carries a
  valid antiforgery token, so the 403 proves the admin-role check denies it);
  admin GET renders seeded users.
- [x] 6.3 Cover: admin create adds a user with `force_password_reset`, stores
  only the hash, redirects to the display page; the display page shows the temp
  password once and a refresh shows nothing. Assert the displayed plaintext
  verifies against the stored credential hash via `IPasswordHasher.Verify`
  (proves the displayed value is the one that was hashed). Also cover the nonce
  guard: stash a temp password, GET `/admin/usercredential` as a non-admin WITH
  the nonce cookie -> 403, then GET as admin -> the password is still available
  (proves the admin check runs before `Take`).
- [x] 6.3a Cover the email-invite create path (email configured via a fake
  `IEmailSender`): the page shows the invite-sent confirmation, no temp password
  is shown, no credential row is stored, `force_password_reset` is set, and a
  `/reset-password?token=` link was sent. Cover the gate: with no
  `AuthEmailService` the create page renders the invite option disabled and a
  forged invite-path POST falls back to showing a temp password (no email sent).
- [x] 6.4 Cover: admin reset-password shows a temp password once (verify it
  against the stored hash via `IPasswordHasher.Verify`), sets
  `force_password_reset`, and revokes sessions; disable revokes sessions; enable
  clears disabled; duplicate email re-renders the create error and adds no user;
  reset/enable/disable against a stale (unknown) id re-renders the list with a
  not-found notice and makes no change.
- [x] 6.5 Cover: POST without an antiforgery token returns 400 (raw POST), and
  the valid path uses `AuthFormTestHelpers.PostFormAsync`; in GitOps read-only
  mode (factory `ReadOnly = true`) an admin POST against a seeded user is not
  409'd AND its effect lands (e.g. disable/enable flips `Enabled`), asserting the
  effect not merely a non-409 status. `RouteMoveReadOnlyTests` is the precedent
  for the `AuthEndpoint` read-only-exemption pattern (non-409 status), not for
  asserting the effect; this test goes further.
- [x] 6.6 Cover: the temp-password plaintext never appears in the captured logs,
  in the create/reset redirect `Location` header, or in any `Set-Cookie` value.
  On the invite path, assert the response/redirect carries no secret and the
  invite token does not appear in the captured logs.

## 7. E2E tests for the admin happy paths

<!-- test(web): add admin user-management browser E2E -->

- [x] 7.1 Add `tests/Freeboard.WebE2E/AdminUserPagesE2ETests.cs` with
  `[Trait("Category", TestCategories.E2E)]` and `[RequiresEnvVarFact(EnvVar =
  E2EGate.EnvVar)]`, extending `E2ETestBase` and calling `Gate()` first.
- [x] 7.2 Drive (temp-password path): seed an admin, log in, create a user, see
  the temp password once, confirm the new user appears in the list, and confirm a
  refresh of the display page shows nothing. Then log in as the new user with the
  displayed temp password and complete the forced reset, proving the displayed
  value is the credential that was set.
- [x] 7.3 Drive (email-invite path), gated on BOTH `FREEBOARD_TEST_E2E` and
  `FREEBOARD_TEST_SMTP` (local Mailpit), skipping cleanly when either is unset:
  with email configured, create a user via the invite option, see the invite-sent
  confirmation (no temp password), read the invite link from the Mailpit HTTP API,
  open `/reset-password`, set a password, and log in as the new user - proving the
  invite link sets a working credential and clears the forced reset.

## 8. Verification

<!-- chore: verify build and tests -->

- [x] 8.1 Run `dotnet build`.
- [x] 8.2 Run `dotnet test --filter
  "Category!=Integration&Category!=E2E&Category!=NFR"` (the Unit tier) and
  confirm it passes.
- [x] 8.3 Run `npx markdownlint-cli2 "openspec/changes/**/*.md"` is not required
  (change files are carved out), but confirm any new repo Markdown passes
  markdownlint.
- [x] 8.4 If a browser and `FREEBOARD_TEST_E2E` are available, run `dotnet test
  --filter "Category=E2E"` for the new E2E tests; the invite E2E additionally
  needs `FREEBOARD_TEST_SMTP` (Mailpit) and skips without it.
  <!-- Ran with Chromium + FREEBOARD_TEST_E2E=1: both admin E2E tests pass (full
  WebE2E suite 12/12 on a clean run). The temp-password test is flaky under CPU
  load due to a pre-existing suite-wide NetworkIdle wait (PasswordFlowsE2ETests
  flakes identically); not a feature bug. Invite path passed every run. F-24
  dropped the FREEBOARD_TEST_SMTP gate (invite reads the in-memory sender). -->

