## Context

`src/Freeboard` is an ASP.NET Core app exposing 35 minimal-API auth endpoints under
`/api/v1/freeboard`, all tagged with the `AuthEndpoint` marker. Authentication is opaque-bearer:
`SessionIssuer.IssueAsync` mints a `v{keyId}.{secret}` token, stores its keyed HMAC in
`sessions.token_hash`, and returns the token once in a JSON body. `BearerAuthenticationHandler`
(scheme `FreeboardBearer`, the only registered scheme) validates `Authorization: Bearer <token>`
against the hash, key version, expiry, user-enabled, and credential epoch, then builds a principal
carrying `user_id`, `session_id`, `role`, and `auth_state`. On a missing bearer it returns
`NoResult`; on an invalid bearer it returns `Fail`. It never issues a login redirect.

`LimitedSessionGuardMiddleware` restricts a force-reset-limited session to three exact API paths:
`GET {prefix}/auth/me`, `POST {prefix}/auth/logout`, `POST {prefix}/account/password`. The match is
exact path + method, so any other route (including a page route) is 403'd for a limited session.
`GitOpsReadOnlyMiddleware` 409s every mutating request unless the matched endpoint carries the
`AuthEndpoint` metadata marker; Razor page POSTs would lack it. Sudo gating is enforced by the
`RequireSudoMode` authorization policy applied to endpoints via `.RequireSudoMode()`
(`RequireSudoModeHandler` reads `sessions.sudo_at` against `Auth:SudoModeTtl`, default 5 minutes),
NOT inside the endpoint delegate body. The endpoint delegates read the client IP from
`ctx.Connection.RemoteIpAddress` for per-IP rate limiting, return distinct status codes (login 202
vs 200, bootstrap 201 vs 409, magic-link-send 400/429), and the passkey/sudo verify delegates read
the raw `ctx.Request.Body` JSON directly. WebAuthn RP id/origin are validated from
`Auth:WebAuthn:Origins`. Email links built by `AuthEmailService` already target
`{baseUrl}/reset-password?token=` and `{baseUrl}/auth/magic-link?token=`.

There are no Razor Pages, no `wwwroot`, and no `UseStaticFiles` today. A browser cannot use a
bearer token returned in a JSON body for navigation, so the backend is unreachable without
screens. The auth services and stores are already in DI (stores singleton; `MfaChallengeService`,
`MfaFactorService`, `WebAuthnCeremony` scoped; `SessionIssuer`, `AuthEmailService` singleton).
`tests/Freeboard.Web.Tests` already has `AuthWebFactory` (a `WebApplicationFactory<Program>`)
with in-memory fakes for every auth store and a `CreateAuthenticatedClient()` helper; its current
tests hard-code `https://localhost`.

## Goals / Non-Goals

**Goals:**

- Add server-rendered self-service auth Razor Pages in `src/Freeboard` covering login (with MFA
  step), logout, password change/forgot/reset, forced-reset completion, login-MFA magic-link
  landing, MFA enrollment/management, sudo step-up, first-admin setup, and session management.
- Add a cookie-to-bearer bridge that reuses the existing session machinery and the unchanged
  `BearerAuthenticationHandler`.
- Add a page-only auth-redirect mechanism, leaving API JSON 401/403 byte-identical.
- Edit `LimitedSessionGuardMiddleware` and `GitOpsReadOnlyMiddleware` additively so the page
  funnel works, with the existing API behaviour preserved exactly.
- Add antiforgery to all form POSTs (and the passkey JS header) and a passkey JS shim confined to
  passkey pages.
- Keep behaviour identical to the API: enumeration-safety, rate limiting, sudo gating,
  force-reset limiting, single-use tokens, status-code distinctions, no token leakage.
- Add Playwright E2E coverage over HTTPS Kestrel with a matching WebAuthn origin, including
  WebAuthn via the CDP virtual authenticator.

**Non-Goals:**

- No reactive framework, no SPA, no Blazor. No new design system. No public-website
  (`Freeboard.Web`) changes. No new backend endpoints or contract changes. No EE code. No
  "remember me", OAuth, or signup/email-verification flows.
- No admin user-management UI (a separate later change). No sudo step-up via magic-link in the
  web UI (cannot work under SameSite=Strict; the backend endpoint stays unscreened).

## Decisions

### Decision 1: Page handlers call the auth services/stores in-process, not the app's own HTTP API

Razor Page handlers inject the same DI services the minimal-API endpoints use
(`IUserStore`, `IPasswordCredentialStore`, `IPasswordHasher`, `SessionIssuer`, `ISessionStore`,
`MfaChallengeService`, `MfaFactorService`, `WebAuthnCeremony`, `AuthRateLimiter`,
`AuthEmailService`, `ITokenHasher`) and run the same logic.

Rationale: self-HTTP would re-serialize, re-authenticate, lose the request's `RemoteIpAddress`
for per-IP rate limiting unless forwarded carefully, and add a network hop and failure mode for
no gain. The project rule is reuse over new code; the services are already the seam.

Risk: the auth logic currently lives inside endpoint delegates, not in callable methods, so a
naive page handler would duplicate it. Mitigation: factor each flow's body into a small internal
method (for example `AuthFlows.LoginAsync(...)` returning a typed result) that BOTH the existing
endpoint delegate and the page handler call. This keeps one implementation; it is a refactor, not
a second system. The endpoints keep their exact current responses.

The shared methods MUST take the cross-cutting inputs as parameters and return a discriminated
result that reproduces the exact endpoint behaviour:

- Client IP (`ctx.Connection.RemoteIpAddress`) is passed in, because per-IP rate limiting is used
  in login, magic-link-send, every verify, sudo, and bootstrap. The page handler reads it from its
  own `HttpContext` and passes it, so rate limiting keys on the real client IP exactly as the API.
- The result type carries the status-code distinctions the endpoints return: login 202 (MFA
  required) vs 200 (full session), bootstrap 201 (created) vs 409 (already initialized),
  magic-link-send 400/429. The endpoint delegate maps the result to the same `IResult`; the page
  handler maps the same result to a screen/redirect.
- The passkey and sudo verifies currently read `ctx.Request.Body` (raw JSON) directly. Pages POST
  form-encoded data, so the page handler marshals the opaque JSON payload itself (the passkey JS
  sends the assertion/attestation JSON) and passes the parsed value into the shared method; the
  endpoint delegate keeps reading the raw body. The shared method takes the already-parsed payload,
  so both callers converge.

The endpoint responses MUST stay byte-identical under the existing endpoint tests.

Alternatives rejected: (a) page handlers call `http://localhost` API - extra hop, IP/rate-limit
fragility, double auth; (b) copy the logic into pages - two implementations that will drift,
violating code-as-liability.

### Decision 2: Cookie bridge via lightweight middleware that injects the bearer header, reusing BearerAuthenticationHandler

A middleware placed BEFORE `UseAuthentication` reads the session cookie and, when present and the
request has no `Authorization` header, sets `Authorization: Bearer <cookie value>` on the request.
`BearerAuthenticationHandler` then validates it unchanged. The cookie value is the same opaque
token `SessionIssuer` already mints; only its HMAC is ever stored. Login/MFA/setup handlers set
the cookie via `Response.Cookies.Append` with the `__Host-` name prefix
(`__Host-freeboard-session`), `HttpOnly=true`, `Secure=true`, `SameSite=Strict`, `Path=/`, no
`Domain`, and an expiry matching the session lifetime. Logout and current-session revoke delete the
cookie. Because `SessionIssuer.IssueAsync` mints a brand-new token (and session row) on every
successful login, MFA completion, and setup, the cookie is always set to a fresh token; no
pre-authentication session identifier is carried forward, so there is no session-fixation surface.

The bridge MUST NOT apply to the JSON API. It runs only for non-API page routes: requests whose
path is under `/api/v1/freeboard/*` are skipped entirely. The skip uses a segment-based check,
`PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`, NOT a
raw string `StartsWith`, so a path that merely shares the prefix text (for example
`/api/v1/freeboardx`) is not misclassified as an API route. The JSON API therefore stays
bearer-header-only and is never reachable by an ambient cookie, so it has no CSRF surface to defend.
API clients keep sending real bearer headers; the middleware never overwrites a present header.

Rationale: the requested constraint is to reuse `ISessionStore`/`SessionIssuer`/`ITokenHasher`
and not invent a second session system. A cookie-only transport over the existing token does
exactly that, and the existing handler already enforces every validation (expiry, revoke, epoch).
The `__Host-` prefix makes the browser enforce Secure + Path=/ + no Domain, so a misconfigured
deployment cannot weaken those attributes.

Alternatives rejected: (a) a full ASP.NET cookie authentication scheme with a serialized
`ClaimsPrincipal` - that is a parallel session system whose claims could drift from the server
session and would bypass the epoch/revoke checks the bearer handler performs on every request;
(b) storing the token in `localStorage` and attaching it via JS - not HttpOnly, exposes the token
to XSS, and breaks no-JS navigation; (c) letting the cookie bridge run on API routes too - that
would put the JSON API behind an ambient credential and force antiforgery on every API mutation,
breaking bearer-only API clients.

### Decision 3: CSRF via ASP.NET Core antiforgery on all cookie-authenticated POSTs

Because the cookie is ambient, form POSTs need CSRF protection. Razor Pages emit an antiforgery
token by default for `<form method="post">`; validation is enabled globally so every page POST
is checked. `SameSite=Strict` on the session cookie is defense in depth, not the only control.
GET handlers stay side-effect-free.

The passkey ceremony POSTs via `fetch`, not a `<form>`, so the automatic hidden antiforgery field
is absent. For the passkey pages the server renders the antiforgery token into the page (via
`IAntiforgery.GetAndStoreTokens` into a meta or data attribute) and `passkey.js` sends it as the
`RequestVerificationToken` request header on its POST. A passkey POST without that header is
rejected by the same global validation.

Rationale: antiforgery is the framework-native, well-tested control; pairing it with
`SameSite=Strict` covers older-browser and edge cases. Bearer-only API calls do not carry the
cookie and are not subject to antiforgery (no cookie, no CSRF surface).

Alternative rejected: relying on `SameSite` alone - weaker on legacy browsers and on any future
cross-subdomain setup.

### Decision 4: Hold the login MFA token server-side, keyed to the browser by a nonce

The login MFA step returns a body-only `mfa_token` that must never be a bearer. The login-MFA
magic-link landing only carries the link token in the URL and needs the matching `mfa_token`.
Store the `mfa_token` in a server-side pending-flow store keyed by a random nonce; the only thing
in the short-lived HttpOnly, `SameSite=Lax` cookie (distinct from the session cookie) is that
nonce. The page reads the nonce from the cookie, looks up the `mfa_token` server-side, and
completes the verify. When no such pending context exists (for example the link is opened in a
different browser, so the nonce cookie is absent or its server entry expired), the landing shows a
restart message rather than attempting to complete.

The nonce cookie is `SameSite=Lax`, not Strict: the login-MFA magic link lands via a cross-site
top-level GET from the email client, and a Strict cookie is not sent on that navigation or its
redirect chain, so a Strict nonce cookie would break the magic-link landing even in the same
browser. Lax sends the cookie on the inbound top-level GET, which is exactly what the landing
needs. The cookie keeps HttpOnly, Secure, a short TTL, and a scoped path.

Server-side here means literally server-held: the `mfa_token` value never travels to the client.
The nonce is opaque and useless without the server entry, so a stolen nonce cookie cannot recover
the `mfa_token`.

`PendingMfaStore` is an in-process singleton backed by `IMemoryCache` with a short TTL, mirroring
the existing `WebAuthnEnrollmentStore` lifetime and pattern. Known limitation: a multi-instance,
non-sticky deployment could land the magic-link round-trip on an instance that never saw the nonce
entry, so the landing shows the restart message; it therefore requires sticky sessions or a later
distributed-cache swap, the same tradeoff `WebAuthnEnrollmentStore` already documents.

The passkey ceremony needs no extra correlation cookie: the passkey correlation is a value the
page already holds and the JS echoes back, and the enrollment/assertion options are already cached
server-side (`WebAuthnEnrollmentStore` / the challenge row). Only the login `mfa_token` is held
server-side via the nonce; no other pending-flow cookie is introduced.

Rationale: keeps the challenge token off the page and out of JS, matches the backend contract
that `mfa_token` is body-only, satisfies "genuinely server-side" without a client-held copy, and
lets the emailed login-MFA link complete in the same browser.

Alternatives rejected: (a) putting `mfa_token` in a hidden form field on the page - readable by any
script and persisted in page source/history; (b) putting the `mfa_token` itself in an HttpOnly
cookie or cookie-TempData - both are client-held, so calling that "server-side" would be false.

### Decision 5: Session cookie SameSite=Strict; sudo-via-magic-link is out of web scope

The session cookie is `SameSite=Strict`. With antiforgery validated on every cookie-authenticated
POST, both Strict and Lax are CSRF-safe, so the choice turns on top-level cross-site GET
navigation. Strict drops the session cookie on the very first cross-site top-level GET (for example
a bookmarked deep link followed from another site): on that first navigation the user appears
unauthenticated until a same-site request, then is authenticated normally. Keep Strict: the cost is
one extra login prompt the first time a user follows an external top-level link into a protected
page; the benefit is the session cookie is never attached to any cross-site-initiated navigation,
which is the stronger default.

This Strict choice applies to the session cookie only. The session cookie is Strict because it
never needs to ride a cross-site navigation - the protected pages are reached same-site once the
user is in. The short-lived landing/pending cookies (the login-MFA nonce, the scrubbed reset and
magic-link token cookies) are `SameSite=Lax` precisely because the emailed-link entry IS a
cross-site top-level GET and a Strict cookie would not be sent on it. This refines the Strict
decision rather than reversing it: Strict for the long-lived session, Lax for the short-lived
landing/pending cookies that must survive an emailed cross-site GET.

Consequence for magic-link: sudo step-up via magic-link cannot work in the web UI. The sudo
magic-link landing requires an already-authenticated session, but under Strict the session cookie
is not sent on the cross-site top-level GET that arrives from an email client. So there is no sudo
magic-link screen. The backend `auth/sudo/magic-link/send` endpoint stays as-is in the API
(unscreened, listed as out-of-scope). The `/account/sudo` step-up screen offers password / TOTP /
passkey / recovery factors only - NOT magic-link.

The `/auth/magic-link?token=` landing therefore handles ONLY the login-MFA magic-link flow. There
is no login-vs-sudo disambiguation and no typed flow discriminator on that landing: it always
completes the login-MFA verify using the held login `mfa_token` (Decision 4) plus the scrubbed link
token (Decision 6). The login-MFA magic-link landing does not depend on the session cookie arriving
on the cross-site GET (the user is not yet logged in; the pending state is the nonce cookie + the
server-held `mfa_token`), so it completes correctly under Strict.

Alternative rejected: `SameSite=Lax` for the SESSION cookie - it attaches the session cookie to
cross-site top-level navigations for no functional gain here; the login-MFA landing already carries
its own pending state (the Lax nonce cookie + the server-held `mfa_token`). The short-lived
landing/pending cookies are a separate case and ARE Lax (Decisions 4 and 6) precisely because they
must ride the emailed cross-site top-level GET.

### Decision 6: Scrub single-use tokens out of the URL on landing, then redirect; consume on POST

`GET /reset-password?token=` and `GET /auth/magic-link?token=` are side-effect-free: each moves the
token immediately into a short-lived HttpOnly transient cookie, then 302s to the same path with no
query string. The bare-path GET renders the form (reset) or completes nothing on its own. The
actual single-use consume happens on an antiforgery-protected POST to the same path, which reads
the token from the transient cookie.

These scrubbed reset-token and magic-link-token transient cookies are `SameSite=Lax` (HttpOnly,
Secure, short TTL, scoped path), not Strict. The emailed link is a cross-site top-level GET from
the email client, and a Strict cookie is not sent on that navigation or the scrub redirect that
follows it; the cookie must ride the emailed cross-site top-level GET and its redirect chain, so it
must be Lax. The bare-path POST that consumes the token is a same-site submit from the rendered
page, so Lax (or Strict) both send the cookie on the POST; Lax is strictly safer here because it is
the only setting that also works on the inbound GET. These transient cookies are not
`__Host-`-prefixed (the `__Host-` prefix does not constrain SameSite anyway); they stay Secure +
HttpOnly + path-scoped. The reset POST consumes the reset token; the magic-link POST
completes the login-MFA verify using the held `mfa_token` plus the scrubbed link token. Splitting
GET-scrub from POST-consume keeps GETs side-effect-free and keeps the single-use token out of
browser history and referrer headers.

What the scrub does and does not protect: the first GET to `/reset-password?token=` /
`/auth/magic-link?token=` necessarily carries the token in the URL before any app code runs, so the
scrub cannot guarantee the token never reaches a reverse-proxy or request log upstream of the app.
The scrub protects browser history, referrer headers, and any post-arrival app log that records
request URLs. To reduce request-log exposure inside this app, the app's own request logging MUST
suppress or redact the `token` query string on these landing paths.

Rationale: single-use tokens are still sensitive in transit through the browser. Getting them out
of the address bar on first paint closes the history/referrer leak without changing the emailed
link contract: `AuthEmailService` keeps building the exact `?token=` URLs, and the scrub happens on
arrival.

Alternative rejected: rendering or keeping the token in the URL/hidden field through the form
submit - leaves it in history and referrers.

### Decision 7: Page-route auth failures redirect via a page-scoped authentication scheme; API JSON 401/403 unchanged

Only `FreeboardBearer` is registered, and its handler returns failure/no-result and does NOT
override `HandleChallengeAsync`/`HandleForbiddenAsync`, so the framework writes a bare 401/403 from
inside the authorization middleware. The spec requires an invalid or absent cookie on a protected
page to redirect to `/login`.

A Razor Pages authorization filter or result filter CANNOT do this: `UseAuthorization` (and
`LimitedSessionGuardMiddleware`) produce the 401/403 in the middleware pipeline, BEFORE any page
handler or page filter runs. By the time a page result filter could execute, the response is
already a bare 401/403. The redirect must therefore come from the authentication scheme that the
authorization challenge invokes, not from a page filter.

Mechanism: add a page-scoped authentication scheme used only for page-route authorization. It is a
forwarding/redirecting scheme (a `PolicyScheme`/forwarding scheme, or a dedicated redirecting
handler) whose only job is to convert the challenge and forbid outcomes into redirects:

- `HandleChallengeAsync` issues `302 /login?returnUrl=...` (the 401 case: no/invalid credentials).
- `HandleForbiddenAsync` issues `302 /account/sudo?returnUrl=...` for the generic
  authorization-forbid case only.

The sudo redirect for a sudo-gated PAGE action does NOT come from `HandleForbiddenAsync`. The
pipeline sudo policy does not run for in-process page handlers (Decision 8), so the page handler's
own in-handler sudo-recency check is the authoritative and sole trigger for `302 /account/sudo` on
those actions. `HandleForbiddenAsync` here exists only to give the page scheme a defined behaviour
if some page route were ever protected by a pipeline forbid policy; no page route in this change
carries such a policy, so this branch is effectively unused. It is kept (not removed) as a safe
default for the scheme, but it is not part of the sudo-gating path - see Decision 8.

The actual credential validation still flows through `FreeboardBearer` (the cookie bridge injects
the bearer header, Decision 2), so this page scheme forwards authentication to `FreeboardBearer` and
overrides only the challenge/forbid responses. Page routes select this scheme for their
authorization challenge via a Razor Pages authorization convention, NOT a process-wide policy.
Define one NAMED authorization policy (for example `"PageAuthenticated"`) whose
`AuthenticationSchemes` is the page challenge scheme and whose requirement is an authenticated user,
and bind it to the protected page folder with a Razor Pages convention
(`RazorPagesOptions.Conventions.AuthorizeFolder("/account", "PageAuthenticated")` /
`PageConventions`). A failed challenge/forbid on those page routes then runs through the page
scheme. This MUST NOT use `AuthorizationOptions.DefaultPolicy` or `FallbackPolicy`: those are
process-wide, so they would route the authorization challenge for non-page routes (for example
`MapGet("/")`, `gitops/status`, and any unattributed API route) through the page scheme, turning
their bare 401 into a redirect and breaking the API byte-identical guarantee. Scoping the policy to
the page folder via a Razor Pages convention is what keeps the page scheme off every non-page route.
The API endpoints keep using `FreeboardBearer` for their authorization, so `/api/v1/freeboard/*`
still emits the JSON 401/403 byte-identical with no redirect. Where a path-based distinction is
still needed (the cookie bridge and the force-reset guard), it uses a segment-based path check
(`PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`),
so a path that merely shares the prefix text is not misclassified.

The force-reset-limited 403 -> `/account/complete-reset` redirect is NOT emitted by this scheme. It
is emitted by `LimitedSessionGuardMiddleware` itself (Decision 9), because that middleware writes
its own 403 and terminates the request before the authorization challenge or any page scheme runs.

Rationale: browsers need a redirect, not a JSON 401; bearer API clients need the JSON 401. A page
result filter is unreachable for the 401/403 because the pipeline writes them first, so the redirect
must live in the scheme the authorization challenge invokes. Scoping the page scheme to page routes
keeps the API responses byte-identical.

### Decision 8: Enforce authorization inside page handlers (pipeline policies do not run for in-process handlers)

The sudo and admin authorization for the API is enforced by pipeline policies attached to the
endpoints (`.RequireSudoMode()`, the admin route-group policy), NOT inside the delegate body. A page
handler that calls the extracted shared flow method directly never triggers those policies. So page
handlers MUST enforce the same authorization explicitly:

- "Must be authenticated" is enforced with the SAME named page policy from Decision 7 (the one whose
  `AuthenticationSchemes` is the page challenge scheme), applied via the Razor Pages
  `AuthorizeFolder` convention - NOT a bare `[Authorize]`. A bare `[Authorize]` uses the default
  policy, whose challenge goes to the default scheme (`FreeboardBearer`, a bare 401), so the
  `/login` redirect would not fire. Naming the page scheme in the policy is what makes the challenge
  redirect.
- For a sudo-gated action, the page handler checks sudo recency BEFORE performing the action, using
  the same check the policy uses. Factor the sudo-recency predicate into a shared method that both
  `RequireSudoModeHandler` and the pages call (or have the page invoke `IAuthorizationService` with
  the `RequireSudoMode` policy). On failure the page handler itself redirects to `/account/sudo` and
  does NOT perform the gated action. This in-handler check is the sole trigger for the sudo redirect
  on page actions; it does not rely on the page scheme's `HandleForbiddenAsync` (Decision 7).

Because the admin UI is out of scope (Decision: admin screens deferred), admin-authz-in-pages is
moot for this change.

Rationale: the gated action must not run without the same authorization the API enforces. A
page-driven MFA enrollment without recent sudo must be rejected/redirected, not silently performed.

### Decision 9: Force-reset and GitOps-read-only middleware edits (additive, API behaviour preserved)

Two existing middlewares block the page funnel and must be edited:

- `LimitedSessionGuardMiddleware` currently allowlists three exact API paths and, for any other
  request from a limited session, writes its own 403 problem+json and terminates - before any
  endpoint, page handler, or the page authentication scheme runs, so nothing downstream can convert
  that 403 into a redirect. This middleware needs two distinct edits:
  - First edit (allow the page routes): add an endpoint-metadata marker ("allowed for limited
    session") to the force-reset page routes (`/account/complete-reset` GET/POST, `/logout`, the
    `/account` landing), and let the middleware also permit a request whose matched endpoint carries
    that marker. The existing exact-path API allowlist is kept unchanged, so a limited session's API
    behaviour is byte-identical; only the marked page routes are additionally allowed. Prefer the
    marker over a second hard-coded page path list.
  - Second edit (redirect the page funnel): for a force-reset-limited session hitting a PAGE route
    that is NOT allowed, the middleware itself responds `302 /account/complete-reset` instead of the
    JSON 403, so the browser is funneled to complete the reset. For `/api/v1/freeboard/*` it keeps
    the existing JSON 403 byte-identical. The middleware distinguishes a page route from an API route
    with the same segment-based check used elsewhere
    (`PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`):
    API prefix -> JSON 403; otherwise -> `302 /account/complete-reset`. This redirect lives here
    because the middleware terminates before the page scheme (Decision 7) could act.

  The two endpoint markers - "allowed for limited session" and `AuthEndpoint` - are independent
  metadata types read by different middlewares (`LimitedSessionGuardMiddleware` and
  `GitOpsReadOnlyMiddleware`), so a single page route (for example the `/account/complete-reset`
  POST) can carry both without interaction. The login page is anonymous because no authorization
  convention is applied to it, not because of any marker.
- `GitOpsReadOnlyMiddleware` 409s every mutating request unless the matched endpoint carries the
  `AuthEndpoint` marker. Razor page POSTs lack it, so in read-only mode all auth page POSTs (login,
  reset, complete-reset, logout, MFA, sudo, setup) 409. Fix: tag the auth page POST routes with the
  existing `AuthEndpoint` marker (Razor Pages conventions support per-page/per-folder metadata).
  The middleware code is unchanged; it exempts the marked page routes exactly as it exempts the API
  auth endpoints. Non-auth mutating routes still get 409; the API behaviour is byte-identical.

Rationale: both fixes reuse the existing endpoint-metadata mechanism rather than adding a parallel
path list, keeping the change small and the API behaviour unchanged.

### Decision 10: Minimal JS only on passkey pages; everything else is plain HTML forms

WebAuthn requires `navigator.credentials`; a small `wwwroot/js/passkey.js` does base64url
encode/decode, calls create/get, and submits an antiforgery-header-protected POST. Add
`UseStaticFiles` to serve it. All other screens use POST-redirect-GET and work with JS disabled.

Alternative rejected: a bundler/SPA - far more liability than the one ceremony needs.

## Screen and route inventory

Each screen maps to an existing backend flow (now shared via the factored internal methods).
Cookie auth supplies the principal; antiforgery guards every POST. Admin screens and the sudo
magic-link screen are out of scope (see Non-Goals).

| Screen | Page route | Backend flow it drives |
| --- | --- | --- |
| Login (+ MFA step) | GET/POST `/login` | login flow; 202 -> MFA challenge, `mfa_token` held server-side |
| MFA challenge: TOTP | POST `/login/mfa/totp` | `auth/mfa/totp` verify |
| MFA challenge: passkey | GET `/login/mfa/passkey` + POST | `auth/mfa/passkey/options` + `auth/mfa/passkey` (JS shim) |
| MFA challenge: recovery | POST `/login/mfa/recovery` | `auth/mfa/recovery` verify |
| MFA challenge: magic-link send | POST `/login/mfa/magic-link` | `auth/mfa/magic-link/send` |
| Magic-link landing (login-MFA only) | GET (scrub) + POST `/auth/magic-link` | `auth/mfa/magic-link/verify` with held login `mfa_token` |
| Logout | POST `/logout` | `auth/logout` + clear cookie unconditionally |
| Account / landing (me) | GET `/account` | `auth/me` |
| Change password | GET/POST `/account/password/change` | `auth/password/change` |
| Forgot password | GET/POST `/forgot-password` | `auth/password/forgot` (uniform) |
| Reset password landing | GET (scrub) + POST `/reset-password` | `auth/password/reset` |
| Forced-reset completion | GET/POST `/account/complete-reset` | `account/password` (force-reset-limited) |
| MFA management | GET `/account/mfa` | `auth/mfa` status |
| TOTP enroll/activate | GET/POST `/account/mfa/totp` | `auth/mfa/totp/enroll` + `.../activate` (sudo) |
| TOTP remove | POST `/account/mfa/totp/remove` | DELETE `auth/mfa/totp` (sudo) |
| Passkey register | GET/POST `/account/mfa/passkey` | `auth/mfa/passkey/register-options` + `/register` (sudo, JS) |
| Passkey remove | POST `/account/mfa/passkey/remove` | DELETE `auth/mfa/passkey/{id}` (sudo) |
| Recovery regenerate | POST `/account/mfa/recovery` | `auth/mfa/recovery/regenerate` (sudo) |
| Sudo step-up | GET/POST `/account/sudo` | `auth/sudo` (+ `/sudo/passkey/options`); password/TOTP/passkey/recovery only |
| Sessions | GET `/account/sessions` | `users/{id}/sessions` list |
| Revoke one / all sessions | POST `/account/sessions/revoke` | DELETE `auth/sessions/{id}` or `users/{id}/sessions` |
| First-admin setup | GET/POST `/setup` | `setup` bootstrap |

Note: the page routes live OUTSIDE the `/api/v1/freeboard` prefix so they do not collide with the
API. The reset and magic-link page routes match the exact paths `AuthEmailService` builds. The
sudo screen drives `auth/sudo` and `auth/sudo/passkey/options`; it does NOT drive
`auth/sudo/magic-link/send` (no web screen).

### Authorization scope: which routes are protected vs anonymous

The named page policy (Decision 7/8) is applied narrowly with `AuthorizeFolder("/account")`, leaving
every other page anonymous by default. The pre-authentication pages are NOT protected: the user is
mid-login and not yet authenticated, so protecting them would redirect-loop (a protected `/login`
would challenge to `/login`). The split is:

- Anonymous (no page policy applied): `/login`, `/login/mfa/totp`, `/login/mfa/passkey`,
  `/login/mfa/recovery`, `/login/mfa/magic-link`, `/auth/magic-link`, `/forgot-password`,
  `/reset-password`, `/setup`, and `/logout`. These are reached before authentication (or, for
  `/logout`, must work for a partially authenticated/limited session), so they stay anonymous.
- Protected by the named page policy (under `/account`, so covered by `AuthorizeFolder("/account")`):
  `/account`, `/account/password/change`, `/account/complete-reset`, `/account/mfa`,
  `/account/mfa/totp`, `/account/mfa/totp/remove`, `/account/mfa/passkey`,
  `/account/mfa/passkey/remove`, `/account/mfa/recovery`, `/account/sudo`, `/account/sessions`,
  `/account/sessions/revoke`.

Scoping the policy to `/account` (option (a)) is chosen over applying it broadly and marking each
public page `AllowAnonymousToPage` (option (b)): all protected routes already share the `/account`
prefix, so one `AuthorizeFolder("/account")` covers them with no per-page anonymous opt-outs to keep
in sync, and a missed opt-out cannot accidentally protect a login page. The sudo-gated `/account/*`
actions additionally enforce sudo recency in their handlers (Decision 8) on top of this
authentication policy.

## File changes

In `src/Freeboard`:

- `Auth/AuthFlows.cs` (new): internal methods extracted from the endpoint delegates so endpoints
  and pages share one implementation; each takes the cross-cutting inputs (client IP, parsed
  passkey/sudo payload) and returns a discriminated result carrying the status-code distinctions.
  The endpoint delegates become thin callers.
- `Web/SessionCookie.cs` (new): the session cookie name (`__Host-freeboard-session`) and
  `__Host-`-compatible options (Secure, HttpOnly, `SameSite=Strict`, Path=/, no Domain);
  set/clear/read. Also the short-lived transient/nonce cookie helpers (scrubbed reset/magic-link
  token cookie, and the login-MFA nonce cookie), which are `SameSite=Lax` (Secure, HttpOnly,
  path-scoped, short TTL, not `__Host-`-prefixed) so they ride the emailed cross-site top-level GET
  and its redirect chain.
- `Web/PendingMfaStore.cs` (new, small): an in-process singleton backed by `IMemoryCache` with a
  short TTL (mirroring `WebAuthnEnrollmentStore`), mapping a random nonce to the held login
  `mfa_token`; the only client-held value is the nonce in its Lax HttpOnly cookie. Known limitation:
  a multi-instance, non-sticky deployment can miss the nonce entry on the magic-link round-trip
  (then the landing shows the restart message), so it requires sticky sessions or a later
  distributed-cache swap - the same tradeoff `WebAuthnEnrollmentStore` documents.
- `Web/SessionCookieMiddleware.cs` (new): copies the cookie token into the `Authorization` header
  before `UseAuthentication` when no header is present, and ONLY for non-API routes (skips
  `/api/v1/freeboard/*` via `PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`).
- `Web/PageChallengeScheme.cs` (new, small): the page-scoped redirecting authentication scheme
  (forwarding to `FreeboardBearer` for validation) whose `HandleChallengeAsync` issues
  `302 /login?returnUrl=...`. Its `HandleForbiddenAsync` issues `302 /account/sudo?returnUrl=...`
  only for the generic authorization-forbid case; the sudo redirect for sudo-gated page actions is
  driven by the in-handler sudo-recency check (Decision 8), so this forbid branch is effectively
  unused. Used only for page-route authorization; the API keeps `FreeboardBearer` so its JSON
  401/403 is byte-identical. The force-reset-limited `302 /account/complete-reset` redirect is NOT
  here - it is emitted by `LimitedSessionGuardMiddleware` (below).
- `Web/LocalRedirect.cs` (new, small): validates a `returnUrl` is local/relative (rejects absolute
  and protocol-relative) and returns a safe default otherwise. One helper, multiple page callers
  (login, sudo resume), so it earns its place over inlining the same check per page.
- `Auth/LimitedSessionGuardMiddleware.cs` (edit): two changes. (1) Also permit a request whose
  matched endpoint carries the new "allowed for limited session" marker; the exact-path API
  allowlist is unchanged. (2) For a force-reset-limited session hitting a non-allowed PAGE route,
  respond `302 /account/complete-reset` instead of the JSON 403; for `/api/v1/freeboard/*` keep the
  JSON 403 byte-identical, distinguished by
  `PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase)`.
- `Pages/` (new): Razor Pages and page models for the screens above, plus `_Layout.cshtml`,
  `_ViewImports.cshtml`, `_ViewStart.cshtml`, and shared partials (error summary, factor list). The
  force-reset page routes carry the "allowed for limited session" marker; the mutating auth page
  routes carry the `AuthEndpoint` marker (so GitOps read-only exempts them).
- `wwwroot/js/passkey.js` (new): the WebAuthn shim; sends the antiforgery `RequestVerificationToken`
  header read from the page.
- `wwwroot/css/auth.css` (new, small): one stylesheet, no framework.
- `Program.cs` (edit): `AddRazorPages` + global antiforgery validation, `AddAntiforgery`, the
  page-scoped redirecting authentication scheme (`Web/PageChallengeScheme.cs`) registered alongside
  `FreeboardBearer`, a NAMED authorization policy whose `AuthenticationSchemes` is the page scheme
  bound to the protected pages with a Razor Pages convention
  (`RazorPagesOptions.Conventions.AuthorizeFolder("/account", "<policy>")`) - NOT
  `AuthorizationOptions.DefaultPolicy`/`FallbackPolicy`, which are process-wide and would redirect
  non-page routes - `UseStaticFiles`, `UseMiddleware<SessionCookieMiddleware>` after `UseRouting`
  and before `UseAuthentication`, `MapRazorPages` with the page-route metadata markers, and
  request-logging redaction for the `token` query string on the landing paths. The API endpoints
  keep `FreeboardBearer` for authorization. No endpoint changes.
- `Freeboard.csproj` (edit): SDK already `Microsoft.NET.Sdk.Web`; ensure Razor Pages build. No new
  package (Razor Pages and antiforgery are in the shared framework).

In `tests`:

- `tests/Freeboard.Web.Tests` (edit): add WebApplicationFactory page tests reusing `AuthWebFactory`
  and the existing fakes; assert cookie attributes, antiforgery enforcement (incl. passkey header),
  redirects (protected page redirect to `/login` vs API JSON 401/403), an anonymous page (`/login`)
  renders without redirect, `/` and `gitops/status` are unchanged for an unauthenticated request
  (folder-scoped policy, not a process-wide default/fallback), limited-session page funnel and its
  `302 /account/complete-reset` vs API JSON 403, GitOps-read-only page POST success, page-driven
  sudo enforcement, and enumeration-safety.
- `tests/Freeboard.WebE2E` (new project): Microsoft.Playwright + xUnit; boots the app over an HTTPS
  Kestrel URL whose origin matches the configured WebAuthn origin (not in-memory TestServer, so the
  Secure `__Host-` cookie sticks), and drives real browser flows, using the CDP virtual
  authenticator for passkey register/assert.
- `Freeboard.slnx` (edit): add the new E2E project.

## Security model

- Session cookie: `__Host-` prefixed, HttpOnly, Secure, `SameSite=Strict`, Path=/, no Domain,
  expiry = session lifetime. Strict because the session cookie never needs to ride a cross-site
  navigation. Holds the same opaque token the API issues; only its HMAC is stored
  server-side. A fresh token is minted on every login, MFA completion, and setup (no
  pre-auth identifier carried forward, so no session fixation). Every request is still fully
  validated by `BearerAuthenticationHandler` (expiry, revoke, user-enabled, credential epoch), so
  cookie auth has no weaker checks than bearer auth. The Secure cookie requires HTTPS; the dev
  profile must serve HTTPS for cookie auth to work.
- API isolation: the cookie-to-`Authorization` bridge runs only for non-API page routes and is
  skipped for `/api/v1/freeboard/*`. The JSON API stays bearer-header-only and is never reachable
  by an ambient cookie, so it carries no CSRF surface.
- Page-auth redirects: a page-scoped redirecting authentication scheme (forwarding to
  `FreeboardBearer`) turns the page-route authorization challenge into `302 /login?returnUrl=...`.
  Page routes select this scheme through a NAMED authorization policy bound to the `/account` folder
  by a Razor Pages `AuthorizeFolder` convention - never `DefaultPolicy`/`FallbackPolicy`, which would
  redirect non-page routes. The sudo `302 /account/sudo` redirect for sudo-gated page actions comes
  from the page handler's in-handler sudo-recency check, not from the scheme's `HandleForbiddenAsync`
  (which is the generic-forbid fallback and effectively unused). The force-reset-limited
  `302 /account/complete-reset` is emitted by `LimitedSessionGuardMiddleware` itself, since it
  terminates the request before any page scheme runs. The API keeps `FreeboardBearer`, so its JSON
  401/403 stays byte-identical.
- Explicit page authorization: pipeline sudo/admin policies do not run for in-process page handlers,
  so page handlers enforce "must be authenticated" via the named page policy (AuthorizeFolder) and
  check sudo recency (the same predicate the policy uses) before any sudo-gated action.
- CSRF: ASP.NET Core antiforgery validated on every page POST, including the passkey fetch POST via
  the `RequestVerificationToken` header; SameSite=Strict as defense in depth. GETs are
  side-effect-free.
- Open-redirect: any `returnUrl` used to resume a flow is validated as a local, relative path
  (reject absolute and protocol-relative URLs) and otherwise replaced with a safe default.
- Token scrub: `/reset-password?token=` and `/auth/magic-link?token=` move the token into a
  short-lived HttpOnly, Secure, `SameSite=Lax`, path-scoped transient cookie (not `__Host-`-prefixed)
  via a side-effect-free GET and 302 to the bare path; the consume happens on a later
  antiforgery-protected POST. Lax (not Strict) because the emailed link is a cross-site top-level GET
  whose redirect chain a Strict cookie would not survive; the consuming POST is a same-site submit,
  so Lax also sends the cookie there. The scrub protects browser history,
  referrers, and post-arrival app logs that record request URLs. It cannot protect a request log
  upstream of the app or this app's own inbound-GET log unless the `token` query string is also
  redacted, which this app does for the landing paths.
- Pending login-MFA state: the login `mfa_token` is held server-side in `PendingMfaStore` (an
  in-process `IMemoryCache` singleton with a short TTL, like `WebAuthnEnrollmentStore`) keyed by a
  random nonce; only the nonce sits in a short-lived HttpOnly, Secure, `SameSite=Lax` cookie (Lax so
  it rides the emailed cross-site top-level GET). The `mfa_token` never reaches the client. Known
  limitation: a multi-instance non-sticky deployment can miss the nonce entry on the round-trip and
  show the restart message, so it needs sticky sessions or a distributed-cache swap.
- No token leakage: the session token, `mfa_token`, magic-link token, reset token, temporary
  passwords, and recovery codes never appear in logs, URLs (the single-use tokens the emailed
  links carry are scrubbed from the URL on arrival and redacted from request logs), or rendered
  HTML beyond the one-time display the backend already performs. Reuse the backend's existing
  "log exception type only" discipline.
- Sudo gating in the UI: sudo-gated screens check sudo recency and redirect to `/account/sudo`
  (password/TOTP/passkey/recovery only) on failure, then resume. The server remains the authority
  via `RequireSudoModeHandler`; the UI never assumes sudo. Sudo via magic-link is out of web scope.
- Force-reset UX: a force-reset-limited session is funneled to `/account/complete-reset`. The page
  routes the session needs (complete-reset GET/POST, logout, the `/account` landing) carry the
  "allowed for limited session" marker so `LimitedSessionGuardMiddleware` permits them; for any other
  PAGE route the middleware itself responds `302 /account/complete-reset` (it terminates before the
  page scheme runs). For `/api/v1/freeboard/*` the middleware keeps the JSON 403 byte-identical,
  distinguished by `PathString.StartsWithSegments(ApiRoutes.ApiRoutePrefix, ...)`. The two endpoint
  markers ("allowed for limited session" and `AuthEndpoint`) are independent metadata types read by
  different middlewares, so one route can carry both without interaction.
- GitOps read-only: the mutating auth page routes carry the `AuthEndpoint` marker so they are
  exempt from the read-only 409 exactly as the API auth endpoints are; non-auth mutating routes
  still 409.
- Enumeration-safety: login and forgot-password screens surface only generic messages; the
  uniform backend responses are preserved by the shared flow.
- WebAuthn: RP id/origins remain server-validated from `Auth:WebAuthn:Origins`; the JS shim only
  marshals bytes. Outside Development the existing startup guard already requires RP config. E2E
  runs over an HTTPS origin matching the configured WebAuthn origin.

## Risks / Trade-offs

- [Auth logic is in endpoint delegates, not callable methods] -> Extract shared internal methods
  first; pass the client IP and parsed payloads in and return a discriminated result; keep endpoint
  responses byte-identical and covered by the existing endpoint tests so the refactor is provably
  behaviour-preserving.
- [Cookie-into-header shim could clobber a real bearer or run on API routes] -> Only set the
  header when absent; the cookie is SameSite=Strict so it is not sent cross-site; API clients send
  real bearers and are unaffected.
- [Editing two existing middlewares could change API behaviour] -> Both edits are additive
  endpoint-metadata exemptions; the exact-path API allowlist and the API 409 behaviour are
  unchanged and asserted by tests.
- [Pipeline policies do not run for in-process page handlers] -> Page handlers enforce auth
  explicitly and check sudo recency before the gated action; a test asserts a page-driven
  enrollment without recent sudo is rejected/redirected.
- [Page-auth redirect could leak into the API path] -> The redirect mechanism is page-route scoped;
  a test asserts `/api/v1/freeboard/*` still returns JSON 401/403.
- [Secure `__Host-` cookie will not stick on plain-HTTP dev] -> Run E2E over HTTPS Kestrel; document
  the HTTPS dev profile requirement.
- [Playwright adds CI weight and a browser dependency] -> Test-only; gate the WebAuthn E2E like
  the existing integration tests so a missing browser skips cleanly rather than failing.
- [Razor Pages + antiforgery misconfig could leave a POST unprotected] -> Enable antiforgery
  validation globally (not per-page) and add tests that an un-tokened form POST and a passkey POST
  without the header are both rejected.

## Migration Plan

Additive only. New pages, middleware, a page-scoped redirecting authentication scheme, static files,
and a test project; two middleware edits; no schema or endpoint contract changes, so no data
migration and no rollback of state. To deploy: ship the build; serve HTTPS so the Secure session cookie sticks; set
`Auth:Email:BaseUrl` to this app's public URL so email links land on the new pages, and set
`Auth:WebAuthn:RpId`/`Origins` for passkeys. Rollback is reverting the build; existing API clients
are unaffected throughout.

## Open Questions

- The shared-flow refactor (Decision 1) is the largest blast radius; confirm the endpoint responses
  stay byte-identical under the existing endpoint tests.
- Confirm the page-route scope of the page-scoped redirecting authentication scheme and the cookie
  bridge stays exactly `/api/v1/freeboard/*`-exclusive (segment-based prefix check) so no API
  response shape changes.
