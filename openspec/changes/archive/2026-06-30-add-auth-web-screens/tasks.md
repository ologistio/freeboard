## 1. Refactor: extract shared auth flows (refactor(web))

- [x] 1.1 Add `src/Freeboard/Auth/AuthFlows.cs` with internal methods extracting the bodies of the
      login, MFA-verify, password change/forgot/reset, account-password, sudo, MFA-enrollment,
      session, and setup endpoint delegates, returning typed results instead of `IResult`. Each
      method takes the cross-cutting inputs as parameters: the client IP (for per-IP rate limiting)
      and, for passkey/sudo verifies, the already-parsed opaque JSON payload. The result type
      carries the status-code distinctions (login 202 vs 200, bootstrap 201 vs 409, magic-link-send
      400/429).
- [x] 1.2 Rewrite the existing endpoint delegates in `AuthEndpoints.cs`, `MfaLoginEndpoints.cs`,
      `MfaEnrollmentEndpoints.cs`, `SudoEndpoints.cs` to call the shared methods and map results to
      the same `IResult` responses (status codes and bodies unchanged). The delegates keep reading
      `ctx.Request.Body` and `ctx.Connection.RemoteIpAddress` and pass them into the shared methods.
- [x] 1.3 Run `dotnet build` and `dotnet test` (existing endpoint tests) to prove the refactor is
      behaviour-preserving (responses byte-identical).

## 2. Cookie session bridge (feat(web))

- [x] 2.1 Add `src/Freeboard/Web/SessionCookie.cs`. Session cookie: name `__Host-freeboard-session`,
      `__Host-`-compatible options HttpOnly, Secure, `SameSite=Strict`, Path=/, no Domain;
      set/clear/read helpers. Plus the short-lived transient/nonce cookie helpers (the scrubbed
      reset/magic-link token cookie and the login-MFA nonce cookie): these are HttpOnly, Secure,
      `SameSite=Lax`, path-scoped, short TTL, and NOT `__Host-`-prefixed - Lax so they ride the
      emailed cross-site top-level GET and its redirect chain (a Strict cookie would not be sent).
- [x] 2.2 Add `src/Freeboard/Web/SessionCookieMiddleware.cs` that, when no `Authorization` header is
      present AND the path is not under the API prefix, copies the session cookie value into
      `Authorization: Bearer <token>`. Detect the API prefix with
      `PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`
      (segment-based, not a raw string `StartsWith`, so `/api/v1/freeboardx`-style paths are not
      misclassified). API routes are skipped so they stay bearer-header-only.
- [x] 2.3 Register the middleware in `Program.cs` after `UseRouting` and before `UseAuthentication`;
      its position relative to `GitOpsReadOnlyMiddleware` does not matter since it only inspects the
      path. Do not change the bearer handler.
- [x] 2.4 Add unit/integration tests: valid cookie authenticates a page request;
      missing/expired/revoked cookie does not; a present `Authorization` header is never
      overwritten; a cookie-only request to an `/api/v1/freeboard/*` route is NOT authenticated by
      the cookie; cookie attributes are `__Host-` prefixed, HttpOnly, Secure, SameSite=Strict,
      Path=/, no Domain; a fresh token is set on login (no pre-auth identifier reused).

## 3. Razor Pages host, layout, antiforgery, page-auth redirect, static files (feat(web))

- [x] 3.1 In `Program.cs` add `AddRazorPages`, `AddAntiforgery`, global antiforgery validation for
      page POSTs, `UseStaticFiles`, and `MapRazorPages`. Keep the API endpoints unchanged.
- [x] 3.2 Add `Pages/_ViewImports.cshtml`, `Pages/_ViewStart.cshtml`, `Pages/Shared/_Layout.cshtml`,
      shared partials (error summary, factor list), and `wwwroot/css/auth.css`. One stylesheet, no
      framework.
- [x] 3.3 Add `src/Freeboard/Web/PageChallengeScheme.cs`: a page-scoped redirecting authentication
      scheme (forwarding to `FreeboardBearer` for validation) whose `HandleChallengeAsync` issues
      `302 /login?returnUrl=`. Its `HandleForbiddenAsync` issues `302 /account/sudo?returnUrl=` only
      for the generic authorization-forbid case; the sudo redirect for sudo-gated page actions is
      driven by the in-handler sudo check (task 8.6), so this forbid branch is effectively unused.
      Register it alongside `FreeboardBearer`. Have protected pages select it via a NAMED
      authorization policy whose `AuthenticationSchemes` is this page scheme, bound to the `/account`
      folder with a Razor Pages convention
      (`RazorPagesOptions.Conventions.AuthorizeFolder("/account", "<policy>")`). Do NOT use
      `AuthorizationOptions.DefaultPolicy`/`FallbackPolicy`: they are process-wide and would route
      the challenge for non-page routes (`/`, `gitops/status`, any unattributed API route) through
      the page scheme, turning their bare 401 into a redirect and breaking the API byte-identical
      guarantee. The API keeps `FreeboardBearer`, so `/api/v1/freeboard/*` JSON 401/403 is
      byte-identical. A Razor Pages result/authorization filter does NOT work here: `UseAuthorization`
      writes the bare 401/403 before any page filter runs. The force-reset-limited
      `302 /account/complete-reset` is NOT in this scheme - it is emitted by
      `LimitedSessionGuardMiddleware` (task 4.1).
- [x] 3.4 Add request-logging redaction for the `token` query string on `/reset-password` and
      `/auth/magic-link` so the inbound landing GET does not record the token.
- [x] 3.5 Prerequisite for the redirect tests below: at least one protected page must already exist
      so "unauthenticated GET redirects to `/login`" can run. Add a minimal protected page here (or
      land the real `/account` page, task 5.4, before this test). Do NOT position the redirect/funnel
      tests before any protected page exists.
- [x] 3.6 Tests: a page POST without a valid antiforgery token is rejected; an unauthenticated GET
      to a protected page redirects to `/login`; an unauthenticated/invalid cookie request to an
      `/api/v1/freeboard/*` route still returns JSON 401/403 (not a redirect); an unauthenticated GET
      to an anonymous page (`/login`) renders (200) and does NOT redirect; unauthenticated requests
      to `/` and `gitops/status` are unchanged (no redirect, original status) - proving the page
      policy is folder-scoped and not a process-wide default/fallback policy.
- [x] 3.7 Scope the named page policy with `AuthorizeFolder("/account", "<policy>")` so only the
      `/account/*` pages are protected. Leave the pre-authentication pages anonymous (no policy):
      `/login`, `/login/mfa/totp`, `/login/mfa/passkey`, `/login/mfa/recovery`,
      `/login/mfa/magic-link`, `/auth/magic-link`, `/forgot-password`, `/reset-password`, `/setup`,
      and `/logout`. Protecting `/login` would redirect-loop to `/login`. Do not use
      `AllowAnonymousToPage` opt-outs; the `/account` prefix already covers every protected route.

## 4. Middleware edits for the page funnel (feat(web))

- [x] 4.1 Edit `LimitedSessionGuardMiddleware` two ways. (1) Add an endpoint-metadata "allowed for
      limited session" marker and apply it to the force-reset page routes (`/account/complete-reset`
      GET/POST, `/logout`, the `/account` landing); permit a request whose matched endpoint carries
      that marker, keeping the exact-path API allowlist unchanged. (2) For a force-reset-limited
      session hitting a non-allowed PAGE route, respond `302 /account/complete-reset` instead of the
      JSON 403; for `/api/v1/freeboard/*` keep the JSON 403 byte-identical. Distinguish page vs API
      with `PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`.
      The "allowed for limited session" and `AuthEndpoint` markers are independent metadata types
      read by different middlewares, so the `/account/complete-reset` POST can carry both.
- [x] 4.2 Tag the mutating auth page POST routes with the existing `AuthEndpoint` marker so
      `GitOpsReadOnlyMiddleware` exempts them from the read-only 409, exactly as it exempts the API
      auth endpoints. No change to the middleware code.
- [x] 4.3 Prerequisite: the limited-session page-funnel tests need the `/account/complete-reset`,
      `/account`, and `/logout` page routes to exist (tasks 5.3, 5.4, 5.8). Run these funnel tests
      only after those pages exist; do NOT position them before the protected pages are created.
- [x] 4.4 Tests: a force-reset-limited cookie session can GET/POST `/account/complete-reset`,
      `/logout`, and the `/account` landing; on any other PAGE route it is redirected
      `302 /account/complete-reset` (not JSON 403); on any other `/api/v1/freeboard/*` route the JSON
      403 is byte-identical to today. A page POST (e.g. login) succeeds under GitOps read-only mode; a
      non-auth mutating route still 409s.

## 5. Login, logout, account, password screens (feat(web))

- [x] 5.1 Login page (GET/POST `/login`): drives the login flow, sets the session cookie on full
      login, renders the MFA challenge screen on 202 with the offered factors, generic errors only.
- [x] 5.2 Hold the login `mfa_token` server-side: add `src/Freeboard/Web/PendingMfaStore.cs`, an
      in-process singleton backed by `IMemoryCache` with a short TTL (mirroring
      `WebAuthnEnrollmentStore`'s lifetime and pattern), mapping a random nonce to the `mfa_token`;
      put only the nonce in a short-lived HttpOnly, Secure, `SameSite=Lax` cookie (Lax so the emailed
      magic-link cross-site top-level GET sends it). Never place the `mfa_token` in a client-readable
      field or in a client-held cookie. Known limitation: a multi-instance non-sticky deployment can
      miss the nonce entry on the magic-link round-trip (the landing then shows the restart message),
      so it requires sticky sessions or a later distributed-cache swap, like `WebAuthnEnrollmentStore`.
- [x] 5.3 Logout (POST `/logout`): revoke the server session and clear the session cookie
      unconditionally (even when the server-side delete is a no-op); carries an antiforgery token.
- [x] 5.4 Account landing (GET `/account`) backed by the me flow.
- [x] 5.5 Change password (GET/POST `/account/password/change`).
- [x] 5.6 Forgot password (GET/POST `/forgot-password`): uniform confirmation regardless of account.
- [x] 5.7 Reset-password landing matching the email link path: on `GET /reset-password?token=`
      (side-effect-free), move the token into a short-lived HttpOnly, Secure, `SameSite=Lax` transient
      cookie (Lax so it rides the emailed cross-site top-level GET and its scrub redirect) and 302 to
      `/reset-password` with no query string, then render the form from the cookie state; on the
      antiforgery-protected `POST /reset-password`, consume and clear the token.
- [x] 5.8 Forced-reset completion (GET/POST `/account/complete-reset`); funnel a force-reset-limited
      session here, then upgrade to full.
- [x] 5.9 Add `src/Freeboard/Web/LocalRedirect.cs`: validate a `returnUrl` is local/relative
      (reject absolute and protocol-relative), else return a safe default. Use it on login and sudo
      resume.
- [x] 5.10 Tests: login no-MFA, login MFA step, invalid creds (generic), rate-limited (generic),
      forgot uniform, reset-token scrub redirect (token leaves the URL) then valid/invalid token,
      forced-reset funnel and upgrade, logout clears cookie even when server delete is a no-op,
      off-site `returnUrl` rejected (open redirect).

## 6. MFA challenge verification screens (feat(web))

- [x] 6.1 TOTP challenge (POST `/login/mfa/totp`).
- [x] 6.2 Recovery challenge (POST `/login/mfa/recovery`).
- [x] 6.3 Magic-link send (POST `/login/mfa/magic-link`) and login-MFA landing: on
      `GET /auth/magic-link?token=` (side-effect-free) scrub the token into a short-lived HttpOnly,
      Secure, `SameSite=Lax` transient cookie (Lax so it rides the emailed cross-site top-level GET)
      and 302 to `/auth/magic-link` with no query string; on the
      antiforgery-protected `POST /auth/magic-link`, complete the login-MFA verify using the held
      login `mfa_token` (looked up via the nonce cookie) plus the scrubbed link token. No
      login-vs-sudo discriminator; show a restart message when no pending login-MFA context exists.
- [x] 6.4 Tests: each factor success path, attempt-cap exhaustion returns to login, magic-link
      scrub redirect then land-and-complete in the same browser, magic-link with no pending context
      shows restart.

## 7. Passkey shim and passkey screens (feat(web))

- [x] 7.1 Add `src/Freeboard/wwwroot/js/passkey.js`: base64url encode/decode,
      `navigator.credentials.create`/`get`, POST with the antiforgery `RequestVerificationToken`
      header read from a meta/data attribute the page renders via `IAntiforgery.GetAndStoreTokens`.
      No other page needs JS.
- [x] 7.2 Passkey challenge screen (GET `/login/mfa/passkey` + POST) using options then assertion.
- [x] 7.3 Passkey register screen (GET/POST `/account/mfa/passkey`, sudo-gated) using
      register-options then register, with optional nickname.
- [x] 7.4 Passkey remove (POST `/account/mfa/passkey/remove`, sudo-gated).
- [x] 7.5 Test: a passkey POST without the `RequestVerificationToken` header is rejected by
      antiforgery.

## 8. MFA management, recovery, sudo step-up (feat(web))

- [x] 8.1 MFA management (GET `/account/mfa`): status (TOTP, passkeys, recovery remaining).
- [x] 8.2 TOTP enroll/activate (GET/POST `/account/mfa/totp`, sudo-gated); show provisioning URI/QR,
      confirm code, display recovery codes once on first-factor activation.
- [x] 8.3 TOTP remove (POST `/account/mfa/totp/remove`, sudo-gated).
- [x] 8.4 Recovery regenerate (POST `/account/mfa/recovery`, sudo-gated), display once.
- [x] 8.5 Sudo step-up (GET/POST `/account/sudo`) supporting password / TOTP / passkey / recovery
      only (NOT magic-link), with a return target.
- [x] 8.6 Enforce sudo in page handlers: before any sudo-gated action, check sudo recency using the
      shared predicate the `RequireSudoMode` policy uses (factor it into a method both the policy
      handler and the pages call, or invoke `IAuthorizationService` with the sudo policy); on
      failure redirect to `/account/sudo` and do NOT perform the action.
- [x] 8.7 Tests: status render, TOTP enroll+activate+recovery-once, a page-driven enrollment without
      recent sudo is rejected/redirected (not silently performed), sudo required then resumed, sudo
      expiry re-prompt.

## 9. Sessions and setup screens (feat(web))

- [x] 9.1 Sessions screen (GET `/account/sessions`): list live sessions, mark current.
- [x] 9.2 Revoke one / all (POST `/account/sessions/revoke`); revoking the current session clears
      the cookie.
- [x] 9.3 First-admin setup (GET/POST `/setup`): bootstrap secret + details; already-initialized and
      wrong-secret messages.
- [x] 9.4 Tests: session list/revoke (incl. current-session cookie clear), setup happy/already/wrong-
      secret.

## 10. Playwright E2E (test(web-e2e), build(web-e2e))

- [x] 10.1 Add `tests/Freeboard.WebE2E` project (Microsoft.Playwright + xUnit) and register it in
      `Freeboard.slnx`.
- [x] 10.2 Boot the app for E2E over an HTTPS Kestrel URL whose origin matches the configured
      WebAuthn origin (not in-memory TestServer, so the Secure `__Host-` cookie sticks), with the
      existing in-memory fakes so E2E needs no MySQL; gate browser-dependent tests to skip cleanly
      when the browser is absent.
- [x] 10.3 E2E: password login -> account; forgot/reset round-trip; forced-reset completion; session
      revoke.
- [x] 10.4 E2E WebAuthn via the CDP virtual authenticator: passkey register, passkey login
      challenge, passkey sudo step-up.
- [x] 10.5 E2E for the non-passkey MFA login factors in a real browser (parity with the passkey
      flows): TOTP login (seed a confirmed TOTP factor, compute a valid code from the staged secret,
      submit on `/login/mfa/totp`, assert a full session); magic-link login (log in as a
      magic-link-eligible MFA user, capture the emailed `/auth/magic-link?token=` link from the
      recording email sender, follow it, complete in the same browser, assert a full session); and
      recovery-code login (consume a seeded recovery code on `/login/mfa/recovery`, assert a full
      session). Non-vacuous asserts; gated like the rest of the E2E suite.

## 11. Verification

- [x] 11.1 `dotnet build` clean.
- [x] 11.2 `dotnet test` green (unit/integration; MySQL/SMTP/Playwright-gated tests skip cleanly when
      their prerequisites are unset).
- [x] 11.3 `npx markdownlint-cli2 "**/*.md"` passes for any new docs.
- [x] 11.4 Confirm no token, recovery code, or temporary password is written to logs or rendered HTML
      beyond the backend's existing one-time display, and the landing `token` query string is
      redacted from request logs.
