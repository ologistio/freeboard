## Why

Freeboard has a complete auth backend (35 endpoints: password login, MFA login and
enrollment, sudo step-up, password reset, magic-link, session and admin management) plus
email delivery, but no screens. A user with only a browser cannot log in, reset a password,
enroll MFA, follow a reset or magic-link email, or manage their sessions. The backend
returns opaque bearer tokens in JSON bodies, which a browser cannot use for navigation. This
change adds the minimum server-rendered self-service auth screens that make the existing
backend usable, and a cookie bridge so a browser can hold a session.

## What Changes

- Add server-rendered Razor Pages to `src/Freeboard` (the existing ASP.NET Core app) for:
  login (incl. the MFA challenge step), logout, password change, forgot-password,
  reset-password landing (matches the existing email link `/reset-password?token=`),
  forced-reset completion, magic-link landing (matches `/auth/magic-link?token=`, login-MFA
  only), MFA enrollment and management (TOTP, passkey, recovery codes), sudo step-up,
  first-admin setup, and account session list/revoke.
- Add a browser session bridge: a cookie-reader middleware stores the existing opaque session
  token in a `__Host-`-prefixed, HttpOnly, Secure, `SameSite=Strict` cookie and presents it to the
  unchanged `BearerAuthenticationHandler` so Razor Page requests authenticate from the cookie. The
  bridge runs only for non-API page routes; it never applies to `/api/v1/freeboard/*` (skipped via a
  segment-based `PathString.StartsWithSegments` check on the API prefix), so the JSON API stays
  bearer-header-only with no cookie/CSRF surface. A fresh token is minted on each
  login/MFA/setup (no pre-auth identifier carried forward). No second session system; the existing
  `SessionIssuer` / `ISessionStore` / `ITokenHasher` machinery is reused as-is.
- Add ASP.NET Core antiforgery (CSRF) protection to all cookie-authenticated form POSTs,
  including a token header the passkey JS POSTs send.
- Edit `LimitedSessionGuardMiddleware` two ways. (1) Let a force-reset-limited cookie session reach
  the force-reset page routes (the complete-reset page, its logout, and the account landing) via an
  endpoint-metadata marker rather than a second hard-coded path list. (2) For a force-reset-limited
  session hitting any other PAGE route, the middleware itself responds `302 /account/complete-reset`
  instead of its JSON 403, because it terminates the request before any endpoint, page, or page-auth
  scheme runs. The existing API behaviour is unchanged: API routes still match the exact allowlist
  paths and otherwise keep the JSON 403, distinguished from page routes by a segment-based API-prefix
  check.
- Edit `GitOpsReadOnlyMiddleware` so the auth page POST routes are exempt from the read-only
  409 the same way the marked API auth endpoints are, by tagging those page routes with the
  existing `AuthEndpoint` metadata marker. Non-auth mutating routes still get 409; the API
  behaviour is byte-identical.
- Add a page-scoped redirecting authentication scheme that turns page-route authorization
  challenge/forbid into browser redirects (challenge/401 -> `/login`, sudo forbid/403 ->
  `/account/sudo`) while leaving `/api/v1/freeboard/*` JSON 401/403 byte-identical. `FreeboardBearer`
  returns failure/no-result and does not override challenge/forbid, so the framework writes a bare
  401/403 from the authorization middleware before any page filter could run; the page scheme
  (forwarding to `FreeboardBearer` for validation) overrides `HandleChallengeAsync` to redirect to
  `/login`. Page routes select this scheme via a NAMED authorization policy whose
  `AuthenticationSchemes` is the page scheme, bound to the protected `/account` pages with a Razor
  Pages `AuthorizeFolder` convention - NOT `AuthorizationOptions.DefaultPolicy`/`FallbackPolicy`,
  which are process-wide and would turn the bare 401 on non-page routes (`/`, `gitops/status`, any
  unattributed API route) into redirects. The API keeps `FreeboardBearer`. The sudo
  `/account/sudo` redirect for sudo-gated page actions comes from the page handlers' in-handler sudo
  check (not the scheme's `HandleForbiddenAsync`); the force-reset-limited `/account/complete-reset`
  redirect is emitted by `LimitedSessionGuardMiddleware`, not this scheme.
- Enforce authorization in page handlers explicitly: protected pages apply the named page policy
  (via `AuthorizeFolder("/account")`) for "must be authenticated", and sudo-gated page actions call
  the same sudo recency predicate the `RequireSudoMode` policy uses before performing the action,
  because the pipeline policies attached to the API endpoints do not run for in-process page
  handlers.
- Scrub single-use tokens off the URL: the `/reset-password?token=` and `/auth/magic-link?token=`
  landings move the token into a short-lived HttpOnly transient cookie via a side-effect-free GET
  and redirect to the bare path before rendering, keeping the token out of browser history,
  referrers, and post-arrival app logs while still matching the emailed link paths exactly. The
  actual consume happens on an antiforgery-protected POST.
- Suppress/redact the token query string in this app's request logging so the inbound landing
  GET does not record the token in request logs.
- Validate any post-login/post-action `returnUrl` as a local, relative path (reject absolute
  and protocol-relative URLs) to prevent open redirects.
- Add minimal vanilla JavaScript, confined to the passkey pages, that calls
  `navigator.credentials.create`/`get` and POSTs the result with the antiforgery header
  (unavoidable WebAuthn progressive enhancement, not a reactive framework).
- Page handlers invoke the existing auth services and stores in-process (the same DI
  singletons/scoped services the minimal-API endpoints use), through shared flow methods
  extracted from the endpoint delegates. They do NOT call the app's own HTTP API. Behaviour
  (uniform responses, rate limiting, enumeration-safety, sudo gating, force-reset limiting,
  status-code distinctions) stays identical to the API.
- Add a Playwright for .NET E2E test project that drives the screens over an HTTPS Kestrel URL
  whose origin matches the configured WebAuthn origin, using the CDP virtual authenticator for
  the WebAuthn flows.

## Capabilities

### New Capabilities

- `auth-web-screens`: server-rendered Razor Pages over the existing auth backend, the
  cookie-to-bearer browser session bridge, antiforgery on form POSTs and the passkey JS header,
  the page-auth redirect mechanism, explicit page-handler authorization, and the screen/route
  inventory mapping each screen to its backend flow.

### Modified Capabilities

<!-- None of the backend endpoint contracts, rate limiting, or crypto change. The edits to
     LimitedSessionGuardMiddleware and GitOpsReadOnlyMiddleware are additive page-route
     exemptions that leave the existing /api/v1/freeboard/* behaviour byte-identical, so no
     existing capability spec needs delta scenarios. -->

## Impact

- Code: `src/Freeboard` gains `Pages/` (Razor Pages), a wwwroot static asset for the passkey
  shim, a cookie-bridge middleware, a page-scoped redirecting authentication scheme, antiforgery and
  Razor Pages service registration, and `UseStaticFiles`. Two existing middlewares are edited:
  - `LimitedSessionGuardMiddleware` gains a metadata-marker allowance for the force-reset page
    routes (the exact-path API allowlist is unchanged) and, for a force-reset-limited session on any
    other page route, emits `302 /account/complete-reset` instead of its JSON 403 while keeping the
    JSON 403 byte-identical for API routes.
  - `GitOpsReadOnlyMiddleware` is unchanged in code; the auth page POST routes are tagged with
    the existing `AuthEndpoint` marker so the middleware exempts them exactly as it exempts the
    API auth endpoints. API behaviour is byte-identical.
  The endpoint delegates are refactored to call shared flow methods, with their responses kept
  byte-identical under the existing endpoint tests. No endpoint contract changes.
- Tests: a new `tests/Freeboard.WebE2E` (Microsoft.Playwright + xUnit) project; new
  WebApplicationFactory-based page tests in the existing web test project.
- Dependencies: Microsoft.Playwright (test-only). Razor Pages and antiforgery ship in the
  ASP.NET Core shared framework, so no new runtime package.
- Licensing: MIT (default). All screens live in `src/Freeboard`, which is MIT; nothing here is
  an enterprise carve-out and nothing references `Freeboard.Enterprise`.
- Config: screens require `Auth:Email:BaseUrl` to point at this app (so email links land on
  these pages) and, for passkeys, `Auth:WebAuthn:RpId`/`Origins`. The `__Host-` Secure session
  cookie requires HTTPS; the local dev profile must serve HTTPS for cookie auth to work. No
  schema or migration changes.

## Non-goals

- No reactive framework (no Blazor, no SPA); SSR Razor Pages with POST-redirect-GET only.
- No changes to `Freeboard.Web` (the AspNetStatic public website) and no public-website content.
- No new design system, CSS framework, or component library; one small static stylesheet, no
  framework.
- No new backend endpoints, no changes to existing endpoint contracts, rate limiting, or crypto.
- No admin user-management UI (user list/create/disable/enable/reset). The backend admin
  endpoints stay as-is; their screens are a separate later change.
- No sudo step-up via magic-link in the web UI. Under SameSite=Strict the session cookie is not
  sent on the cross-site top-level GET from an email client, and the sudo magic-link landing
  needs an already-authenticated session, so it cannot work. The backend
  `auth/sudo/magic-link/send` endpoint stays as-is in the API but gets no screen. The
  `/account/sudo` step-up screen offers password / TOTP / passkey / recovery factors only.
- No "remember me", OAuth/social login, or email-verification-on-signup flows (the backend has
  none).
- No JavaScript beyond the passkey ceremony shim; no client-side form validation framework.
