# auth-web-screens Specification

## Purpose
TBD - created by archiving change add-auth-web-screens. Update Purpose after archive.
## Requirements
### Requirement: Browser session bridge from an opaque session token in a cookie

The system SHALL let a browser hold an authenticated session by storing the existing opaque
session token (the `v{keyId}.{secret}` value minted by `SessionIssuer`) in a cookie and
presenting it to the existing bearer authentication on each Razor Page request. The system MUST
NOT introduce a second session store, token format, or hashing scheme; it reuses
`SessionIssuer`, `ISessionStore`, and `ITokenHasher` unchanged.

The session cookie MUST use the `__Host-` name prefix and be `HttpOnly`, `Secure`,
`SameSite=Strict`, and `Path=/` with no `Domain` attribute, with no client-readable copy of the
token anywhere in the page, URL, or logs. A fresh session token MUST be issued on each successful
login, MFA completion, and setup; the cookie is set to that new token. The system MUST NOT carry a
pre-authentication session identifier forward into the authenticated session.

The cookie-to-`Authorization` bridge MUST apply ONLY to non-API page routes. It MUST NOT
authenticate any request under `/api/v1/freeboard/*`; the JSON API stays bearer-header-only so it
is never reachable by an ambient cookie and has no CSRF surface.

#### Scenario: Cookie token authenticates a page request

- **WHEN** a request to a protected Razor Page carries a valid session cookie and no
  `Authorization` header
- **THEN** the request is authenticated as the cookie's session via the existing bearer handler
  and the page renders for that user

#### Scenario: Cookie does not authenticate an API route

- **WHEN** a request to a `/api/v1/freeboard/*` route carries only the session cookie and no
  `Authorization` header
- **THEN** the bridge does not run, no `Authorization` header is synthesized, and the request is
  treated as unauthenticated by the JSON API

#### Scenario: Invalid or expired cookie is rejected

- **WHEN** a request to a protected page carries a session cookie whose token is unknown, expired,
  revoked, or whose credential epoch is stale
- **THEN** the request is treated as unauthenticated and the browser is redirected to `/login`

#### Scenario: Cookie is never exposed to client script or URLs

- **WHEN** any authenticated page is rendered
- **THEN** the session token does not appear in the HTML, in any URL, in query strings, or in
  application logs, and the cookie is marked HttpOnly, Secure, SameSite=Strict, and `__Host-`
  prefixed

#### Scenario: Fresh session token on each login

- **WHEN** a user completes a login, an MFA challenge, or first-admin setup
- **THEN** a new session token is minted by `SessionIssuer` and the session cookie is set to that
  new token, with no reuse of any pre-authentication session identifier

### Requirement: Page-route auth failures redirect; API JSON responses unchanged

The system SHALL convert page-route auth failures into browser redirects, because the bearer
authentication that validates the credential returns failure or no-result and never issues a login
redirect. Page-route redirects are produced by three distinct sources: an unauthenticated or
invalid request to a protected page redirects to `/login`, produced by the page-scoped challenge
authentication scheme; a force-reset-limited session on a non-allowed page route redirects to
`/account/complete-reset`, produced by the limited-session guard middleware itself before any page
authorization runs (the force-reset guard terminates the request first); and a sudo-gated page
action without a recent sudo step-up redirects to `/account/sudo`, produced by the page handler's
own sudo-recency check rather than by the page authorization challenge/forbid scheme (whose forbid
handler is a generic, effectively-unused fallback). All of these apply only to page routes; requests
under `/api/v1/freeboard/*` MUST keep returning the existing JSON 401/403 byte-identical.

#### Scenario: Unauthenticated page request redirects to login

- **WHEN** a request to a protected page has no valid session cookie
- **THEN** the browser is redirected to `/login` with a local return target, not given a JSON 401

#### Scenario: API auth response is unchanged

- **WHEN** a request to a `/api/v1/freeboard/*` route is unauthenticated or carries an invalid
  bearer
- **THEN** the JSON 401/403 response is byte-identical to the existing API behaviour and no redirect
  is issued

### Requirement: Force-reset and GitOps page-route exemptions preserve API behaviour

The system SHALL let a force-reset-limited cookie session reach the force-reset page routes (the
complete-reset page GET and POST, logout, and the account landing) and SHALL keep all auth page
POSTs working in GitOps read-only mode, by tagging those page routes with endpoint metadata that
the existing middlewares already honor. The existing exact-path API allowlist for a limited session
and the existing GitOps read-only 409 behaviour for the API MUST stay byte-identical.

#### Scenario: Limited session reaches the force-reset page funnel

- **WHEN** a force-reset-limited cookie session requests `/account/complete-reset` (GET or POST),
  `/logout`, or the account landing
- **THEN** the request is permitted and the page runs

#### Scenario: Limited session on a non-allowed page route is redirected

- **WHEN** a force-reset-limited cookie session requests any other PAGE route (not in the
  force-reset allowlist and not under `/api/v1/freeboard/*`)
- **THEN** the force-reset guard responds `302 /account/complete-reset` (not a JSON 403), funneling
  the browser to complete the reset

#### Scenario: Limited session on a non-allowed API route keeps the JSON 403

- **WHEN** a force-reset-limited cookie session requests any `/api/v1/freeboard/*` route outside the
  exact-path API allowlist
- **THEN** the force-reset guard returns the JSON 403 byte-identical to the existing API behaviour,
  with no redirect

#### Scenario: Auth page POST works under GitOps read-only mode

- **WHEN** GitOps read-only mode is on and a user submits an auth page POST (for example login)
- **THEN** the POST is exempt from the read-only 409 and runs, while a non-auth mutating route still
  returns 409

### Requirement: Page handlers enforce authorization explicitly

Page handlers SHALL enforce the same authorization themselves, because the pipeline authorization
policies attached to the API endpoints (the sudo-mode policy) do not run for in-process page
handlers: a sudo-gated action MUST verify a recent sudo step-up - using the same recency check the
sudo policy uses - before performing the action, and MUST redirect to the sudo step-up screen
otherwise.

#### Scenario: Page-driven sudo-gated action without recent sudo is refused

- **WHEN** a user invokes a sudo-gated page action (for example MFA enrollment) without a recent
  sudo step-up
- **THEN** the action is not performed and the user is redirected to `/account/sudo`, then returned
  to the action after stepping up

### Requirement: Login screen with MFA challenge step

The system SHALL render a login screen that submits email and password and, on success, sets the
session cookie and redirects to the post-login landing page. When the backend returns the MFA
challenge (HTTP 202 equivalent), the system SHALL hold the challenge token server-side (not in a
bearer-usable client field) and render the MFA challenge screen offering exactly the factors the
challenge reports.

#### Scenario: Password login without MFA

- **WHEN** a user submits valid credentials for an account without MFA
- **THEN** a full session is issued, the session cookie is set, and the user is redirected to the
  landing page

#### Scenario: Password login requiring MFA

- **WHEN** a user submits valid credentials for an MFA-enabled account
- **THEN** the user is shown the MFA challenge screen listing only the offered factors, and no
  session cookie is set until a factor is verified

#### Scenario: Invalid credentials

- **WHEN** a user submits an unknown email or wrong password
- **THEN** a generic "invalid email or password" error is shown that does not reveal whether the
  account exists, and no session cookie is set

#### Scenario: Rate-limited login

- **WHEN** the backend rate limiter throttles the attempt
- **THEN** the screen shows a generic "too many attempts, try again later" message without
  leaking account existence

### Requirement: MFA challenge verification screens

The system SHALL provide verification for each challenge factor the backend supports: TOTP code,
passkey assertion, recovery code, and the magic-link fallback (request send, then land via the
emailed link). On a successful factor the system SHALL set the session cookie and redirect to the
landing page.

#### Scenario: TOTP challenge succeeds

- **WHEN** a user enters a valid TOTP code on the challenge screen
- **THEN** a full session is issued, the cookie is set, and the user reaches the landing page

#### Scenario: Recovery code challenge succeeds

- **WHEN** a user enters a valid unused recovery code
- **THEN** the code is consumed, a full session is issued, and the cookie is set

#### Scenario: Passkey challenge succeeds

- **WHEN** a user completes the passkey assertion on the challenge screen
- **THEN** the assertion is verified, a full session is issued, and the cookie is set

#### Scenario: Magic-link login-MFA fallback challenge

- **WHEN** a user requests the magic-link factor and then opens the emailed
  `/auth/magic-link?token=` link in the same browser
- **THEN** the side-effect-free GET moves the link token into a short-lived transient cookie
  (HttpOnly, Secure, `SameSite=Lax`, short TTL, path-scoped) and redirects to `/auth/magic-link`
  with no query string, and an antiforgery-protected POST completes the login-MFA verify using the
  held login `mfa_token` plus the scrubbed link token, after which a full session is issued and the
  cookie is set

#### Scenario: Magic-link landing without pending login-MFA context

- **WHEN** a user opens the emailed `/auth/magic-link?token=` link in a browser with no pending
  login-MFA context (for example a different browser, so the held `mfa_token` is not reachable)
- **THEN** the screen shows a restart message and no session is issued

#### Scenario: Exhausted challenge attempts

- **WHEN** a user exceeds the backend's per-challenge attempt cap
- **THEN** the challenge is consumed and the user is returned to the login screen to start over

### Requirement: Password change, forgot, reset, and forced-reset screens

The system SHALL provide: a password-change screen for an authenticated user (old + new
password); a forgot-password screen that always shows the same confirmation regardless of account
existence; a reset-password landing screen at `/reset-password?token=` that sets a new password
from the emailed token; and a forced-reset completion screen for a force-reset-limited session.

When a landing screen is opened with the token in the query string
(`GET /reset-password?token=`), the system SHALL treat the GET as side-effect-free: move the token
out of the URL into a short-lived transient cookie (HttpOnly, Secure, `SameSite=Lax`, short TTL,
path-scoped), then 302 to the same path with no query string, and render the form from the cookie
state; the single-use token is consumed only on the
later antiforgery-protected POST. This keeps the single-use token out of browser history and
referrer headers. The scrub cannot remove the token from a request log upstream of the app or this
app's own inbound-GET log, so this app's request logging MUST redact the `token` query string on
the landing paths. The emailed URL paths that `AuthEmailService` builds MUST continue to work
exactly.

#### Scenario: Authenticated password change

- **WHEN** an authenticated user submits a correct old password and a new password
- **THEN** the password is changed, other sessions are revoked, and the current session keeps
  working

#### Scenario: Forgot-password is enumeration-safe

- **WHEN** a user submits any email to the forgot-password screen
- **THEN** the same confirmation message is shown whether or not the account exists, and no signal
  distinguishes the two

#### Scenario: Reset-password landing scrubs the token from the URL

- **WHEN** a user opens `/reset-password?token=<token>`
- **THEN** the side-effect-free GET moves the token into a short-lived transient cookie (HttpOnly,
  Secure, `SameSite=Lax`, short TTL, path-scoped) and the browser is redirected to `/reset-password`
  with no query string, the form renders from the cookie state, and the app's request logging
  redacts the `token` query string

#### Scenario: Reset-password landing consumes the emailed token

- **WHEN** a user, after the scrub redirect, submits a new password
- **THEN** the token held in the transient cookie is consumed once, the password is set, all
  sessions are revoked, the transient cookie is cleared, and the user is sent to the login screen

#### Scenario: Invalid or expired reset token

- **WHEN** a user opens the reset landing with a missing, used, or expired token and submits
- **THEN** an "invalid or expired link" error is shown and no password is changed

#### Scenario: Forced reset is required before normal use

- **WHEN** a user whose account requires a password reset signs in
- **THEN** they can only reach the forced-reset completion screen (plus view-self and logout)
  until they set a new password, after which their session is upgraded to full

### Requirement: MFA enrollment and management screens

The system SHALL provide an MFA management screen showing current factor status (TOTP confirmed,
passkeys with nicknames, recovery codes remaining) and flows to enroll TOTP (show provisioning
URI / QR, then confirm a code), register a passkey, regenerate recovery codes, and remove a
factor. The system SHALL display recovery codes returned on first-factor activation exactly once.
All state-changing enrollment actions MUST require a recent sudo step-up.

#### Scenario: View MFA status

- **WHEN** an authenticated user opens the MFA management screen
- **THEN** it shows whether TOTP is confirmed, the list of registered passkeys, and the number of
  remaining recovery codes

#### Scenario: Enroll TOTP

- **WHEN** an authenticated user with recent sudo enrolls TOTP and confirms a valid code
- **THEN** TOTP is activated; if this is the first factor, the recovery codes are shown once

#### Scenario: Register a passkey

- **WHEN** an authenticated user with recent sudo completes the passkey registration ceremony
- **THEN** the passkey is stored and appears in the status list

#### Scenario: Sudo required for changes

- **WHEN** a user attempts an enrollment change without a recent sudo step-up
- **THEN** they are sent to the sudo step-up screen and returned to the action after stepping up

#### Scenario: Recovery codes shown once

- **WHEN** recovery codes are generated on first-factor activation or regeneration
- **THEN** they are displayed once on that response and are not retrievable again from the UI

### Requirement: Sudo step-up screen

The system SHALL provide a sudo step-up screen that re-confirms one of the user's available
factors (password for a non-MFA user, or TOTP / passkey / recovery for an MFA user) and, on
success, allows the user to continue to the sudo-gated action they requested. Sudo step-up via
magic-link is out of web scope: under the SameSite=Strict session cookie the cross-site top-level
GET from an email client does not carry the session the sudo landing would require, so the screen
does not offer magic-link. The backend `auth/sudo/magic-link/send` endpoint is unchanged but has no
web screen.

#### Scenario: Step up with a factor

- **WHEN** a user completes a valid factor on the sudo screen
- **THEN** the session is stamped with sudo and the user is returned to the originally requested
  sudo-gated screen

#### Scenario: Sudo expires

- **WHEN** a user's last sudo step-up is older than the configured sudo window and they request a
  sudo-gated action
- **THEN** they are prompted to step up again before the action proceeds

### Requirement: Session and account management screen

The system SHALL provide a screen listing the user's live sessions (created, last-seen, expiry,
and whether each is the current session) with the ability to revoke a single session or all
sessions. Revoking the current session SHALL also clear the session cookie. The logout POST SHALL
carry an antiforgery token and SHALL clear the `__Host-freeboard-session` cookie unconditionally,
even when the server-side session delete is a no-op.

#### Scenario: List sessions

- **WHEN** an authenticated user opens the sessions screen
- **THEN** their live sessions are listed with timestamps and the current session is identified

#### Scenario: Revoke another session

- **WHEN** a user revokes a session that is not the current one
- **THEN** that session is deleted and the list refreshes without it

#### Scenario: Logout clears cookie and server session

- **WHEN** a user logs out
- **THEN** the server session is revoked and the session cookie is cleared so the browser is no
  longer authenticated, and the cookie is cleared even if the server-side delete was a no-op

### Requirement: First-admin setup screen

The system SHALL provide a setup screen that creates the first admin from an email, name,
password, and the out-of-band bootstrap secret, and on success signs the new admin in. When a
first admin already exists, the screen SHALL report that setup is already complete.

#### Scenario: Bootstrap the first admin

- **WHEN** the correct bootstrap secret and valid details are submitted and no admin exists yet
- **THEN** the admin account is created, a session cookie is set, and the user reaches the landing
  page

#### Scenario: Setup already complete

- **WHEN** setup is attempted after a first admin already exists
- **THEN** an "already initialized" message is shown and no account is created

#### Scenario: Wrong bootstrap secret

- **WHEN** an absent or wrong bootstrap secret is submitted
- **THEN** setup is rejected without revealing whether an admin exists

### Requirement: Antiforgery protection on cookie-authenticated form POSTs

Every state-changing POST authenticated by the ambient session cookie SHALL carry and validate an
ASP.NET Core antiforgery token. A POST with a missing or invalid antiforgery token MUST be rejected
before any auth action runs. The passkey ceremony POSTs via `fetch` rather than a `<form>`, so the
page SHALL render the antiforgery token (via `IAntiforgery.GetAndStoreTokens`) and the passkey JS
SHALL send it as the `RequestVerificationToken` request header.

#### Scenario: Valid antiforgery token accepted

- **WHEN** a form POST includes the matching antiforgery token
- **THEN** the request proceeds to the page handler

#### Scenario: Missing or invalid antiforgery token rejected

- **WHEN** a form POST omits or carries an invalid antiforgery token
- **THEN** the request is rejected with an antiforgery failure and no auth state changes

#### Scenario: Passkey POST without the antiforgery header rejected

- **WHEN** a passkey ceremony POST omits the `RequestVerificationToken` header
- **THEN** the request is rejected with an antiforgery failure and no auth state changes

### Requirement: Local-only return-URL validation

Any `returnUrl` used to resume a user after login or a sudo step-up MUST be validated as a local,
relative path before redirect. The system MUST reject absolute URLs and protocol-relative URLs
(`//host/...`) and fall back to a safe default path. This prevents the auth screens from being
used as an open redirect.

#### Scenario: Local return URL is honored

- **WHEN** a flow carries a `returnUrl` that is a local relative path
- **THEN** the user is redirected to that path after the flow completes

#### Scenario: Off-site return URL is rejected

- **WHEN** a flow carries a `returnUrl` that is absolute or protocol-relative
- **THEN** the off-site target is rejected and the user is redirected to a safe local default
  instead

### Requirement: Passkey ceremony JavaScript shim

The system SHALL serve a minimal vanilla-JavaScript file, loaded only on the passkey screens,
that calls `navigator.credentials.create` for registration and `navigator.credentials.get` for
assertion, encodes the result, and POSTs it back to the page handler. No other screen SHALL
require JavaScript to function.

#### Scenario: Passkey registration via the shim

- **WHEN** a user on the passkey enrollment screen invokes the shim with server-provided creation
  options
- **THEN** the browser credential is created and the encoded attestation is POSTed to the
  registration handler

#### Scenario: Passkey assertion via the shim

- **WHEN** a user on a passkey challenge or sudo screen invokes the shim with server-provided
  assertion options
- **THEN** the browser assertion is produced and the encoded result is POSTed to the verification
  handler

#### Scenario: Non-passkey screens need no JavaScript

- **WHEN** any non-passkey screen is used with JavaScript disabled
- **THEN** the screen still functions through plain HTML form submission

