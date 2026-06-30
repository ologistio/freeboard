## Context

The web app (`src/Freeboard`, MIT) already has:

- `src/Freeboard/Auth/UserAdminEndpoints.cs` - the JSON user-admin endpoints
  (list/create/get/disable/enable/reset-password), each behind
  `GlobalRoles.AdminPolicy` ("RequireAdmin"). Create and reset-password generate
  a one-time temp password via `TempPassword.Generate()`, store only its
  Argon2id hash (`IPasswordHasher` + `IPasswordCredentialStore`), set
  `force_password_reset`, and revoke sessions. No email is sent.
- The browser session funnel from `add-auth-web-screens`: a `__Host-` session
  cookie carries the opaque bearer token; `SessionCookieMiddleware` bridges it to
  the `Authorization` header for non-API page routes only;
  `BearerAuthenticationHandler` validates it and populates the principal with
  `AuthClaims.UserId`, `AuthClaims.SessionId`, and `AuthClaims.Role`.
- `Pages/` auth screens, with a global antiforgery convention
  (`AutoValidateAntiforgeryTokenAttribute`) and `AuthorizeFolder("/Account",
  PageChallengeScheme.PolicyName)` applied in `Program.cs`.
- `src/Freeboard/Web/RecoveryCodeDisplayStore.cs` - the one-time display pattern:
  stash a value keyed by a nonce in `IMemoryCache` (short TTL, `StrongBox` +
  `Interlocked.Exchange` so a value is claimed exactly once), set a path-scoped
  transient nonce cookie (`SessionCookie.SetTransient`), redirect to a display
  page that calls `Take(nonce)` and clears the cookie.

There is no browser UI to manage users. This change adds admin pages over the
same store/flow layer.

A central constraint: for Razor Page handlers, an authorization policy attached
to the route runs only as the folder/page authorize convention, and the page
authorization scheme (`PageChallengeScheme`) turns a forbid outcome into a
redirect to `/account/sudo` (its `HandleForbiddenAsync`). That redirect is a
sudo step-up fallback, not an admin gate. So an admin-role check expressed as a
folder authorize policy would send a non-admin to the sudo page rather than
denying access. The existing code already handles per-handler gating in-page
(for example `Pages/Account/Mfa/Recovery.cshtml.cs` checks sudo recency inside
`OnPostAsync` and redirects itself). This change follows that pattern for the
admin-role gate.

## Goals / Non-Goals

**Goals:**

- Server-rendered admin pages for list/create/enable/disable/reset-password,
  reusing the existing store/flow layer (no API-over-HTTP call, no duplicated
  validation or credential logic).
- Admin-role authorization enforced in-page in every handler.
- Antiforgery validated on every POST.
- One-time temp-password handoff reusing the recovery-code display-store pattern.
- Unit and E2E coverage. Unit tests are deliberately untagged (selected by the
  exclusion filter); only the E2E tests carry `Category=E2E`.

**Non-Goals:**

- No behavioral or API-contract change to `UserAdminEndpoints`, `AuthFlows`, or
  the stores. The create/reset bodies are refactored into shared `AuthFlows`
  helpers, but the JSON contract, status codes, and observable behavior stay
  identical.
- New user-management features (delete, roles editor, bulk actions, audit).
- Emailing the generated temporary password. (The email-invite path emails a
  single-use set-password link, not a temp password; see the create
  credential-handoff decision.)
- A new invite token format, token store, or accept page. The invite reuses the
  existing password-reset token store and `/reset-password` page.
- Client-side reactivity / SPA.
- Any EE code. This is MIT and lives in `src/Freeboard`.

## Decisions

### Pages over the store/flow layer, not over HTTP

The page handlers inject the same services the endpoints use (`IUserStore`,
`IPasswordCredentialStore`, `IPasswordHasher`, `ISessionStore`,
`AuthCryptoOptions`/`IServiceProvider` for the current secret version) and call
the same operations. The auth screens already do this (they call `AuthFlows`,
not the API). The shared create/reset logic in `UserAdminEndpoints` is currently
private static methods on that class.

To avoid duplicating the create-and-handoff and reset-and-handoff sequences in
two places, extract them into small shared helpers the endpoints and the pages
both call. Two reasonable shapes:

- Option A: add `AuthFlows`-style static methods (e.g.
  `AuthFlows.CreateUserAsync` / `AuthFlows.ResetUserPasswordAsync`) returning a
  result that carries the user and the one-time plaintext, and have both
  `UserAdminEndpoints` and the page models call them. This matches the existing
  `AuthFlows` pattern (the API and pages already share via `AuthFlows`).
- Option B: keep the logic only in `UserAdminEndpoints` and have the pages call
  the endpoint handlers directly.

Decision: Option A, with the shared methods on `AuthFlows`. It keeps one
implementation of the credential handoff, matches the existing `AuthFlows`
sharing seam the auth screens already use, and keeps `UserAdminEndpoints` thin.
Option B is rejected: the endpoint handlers return `IResult` shaped for JSON
(status codes, problem bodies) which a page cannot reuse cleanly, so calling them
would force the page to interpret an `IResult`.

#### Which operations route through `AuthFlows`, and which call the stores directly

Only the two operations with a credential handoff and field validation move into
shared `AuthFlows` methods. Enable, disable, and list are simple store flips with
no validation or plaintext to share, so they stay as direct store calls in both
the endpoint and the page (extracting them would be a single-caller-pair helper
against code-as-liability):

- `AuthFlows.CreateUserAsync(...)` -> `CreateUserResult` (field validation +
  duplicate-email + credential handoff). Shared by `UserAdminEndpoints` and the
  create POST handler.
- `AuthFlows.ResetUserPasswordAsync(...)` -> `ResetUserPasswordResult` (unknown-id
  + credential handoff + session revoke). Shared by `UserAdminEndpoints` and the
  reset POST handler.
- enable / disable: page handler calls `IUserStore.SetEnabledAsync` (and, for
  disable, `ISessionStore.DeleteAllForUserAsync`) directly, after a
  `GetByIdAsync` existence check - same calls the endpoint makes today. No shared
  helper.
- list: page handler calls `IUserStore.ListAsync` directly. No shared helper.

#### Create result is a discriminated union, not just user + plaintext

`UserAdminEndpoints.CreateUserAsync` today interleaves field validation
(email/name/role), a duplicate-email pre-check, a `MySqlException` duplicate-key
catch, and the credential handoff, each returning a JSON-shaped `IResult`. The
create page needs the same outcomes to re-render the form, so the shared method
returns a result-record union mirroring the existing
`BootstrapResult.Invalid(IDictionary<string, string[]>)` shape
(`AuthFlows.cs` ~line 393), not a bare user + plaintext tuple:

```text
CreateUserResult:
  Success(UserRow User, string TemporaryPassword)  // temp-password path
  Invited(UserRow User)                            // email-invite path: link sent, no plaintext
  InviteSendFailed(UserRow User)                   // email-invite path: row created, send failed
  Invalid(IDictionary<string, string[]> Errors)    // missing email/name, unknown role
  DuplicateEmail                                    // pre-check OR duplicate-key catch
```

The page selects the handoff via a request argument; the shared method picks the
branch (temp password vs invite) and surfaces `Success` or `Invited`. The JSON
endpoint always requests the temp-password path, so its contract is unchanged
(`Invited` and `InviteSendFailed` are never returned to the API). The page maps
`Invited` to the invite-sent confirmation and `Success` to the display-page
redirect.

On the invite branch the user row is created (with `force_password_reset` and no
password) BEFORE both the token mint and the email send. Token mint and email
send are treated as one invite-provisioning step: if EITHER
`IPasswordResetStore.CreateAsync` (token mint) or `SendInviteAsync` (email send)
throws, `CreateUserAsync` returns `InviteSendFailed(UserRow)` rather than
`Invited`. The row already exists and is enabled, so the recovery is the per-user
reset-password action, not a re-create (re-create is blocked by the
duplicate-email pre-check; see the invite-provisioning-failure decision below).

- The endpoint maps `Invalid` and `DuplicateEmail` to
  `ApiResponses.ValidationProblem(...)` (the same 422 bodies it returns today) and
  `Success` to the 201 JSON. The duplicate-key `MySqlException` catch lives inside
  `CreateUserAsync` and is surfaced as `DuplicateEmail`, so the endpoint keeps no
  MySQL knowledge.
- The page maps `Invalid`/`DuplicateEmail` to a re-rendered create form with the
  field errors, and `Success` to the temp-password display redirect.

#### Unknown user id on reset / enable / disable

The API handlers return `Results.NotFound()` for an unknown id
(`UserAdminEndpoints.cs:113,128,144`). A user can be deleted between the list
render and a POST (stale id). The page must handle a missing id explicitly:

- `ResetUserPasswordResult` carries an `UnknownUser` arm; the endpoint maps it to
  `Results.NotFound()` (unchanged) and the page re-renders the list with a
  not-found notice (the target row is gone, so there is nothing to act on).
- enable / disable: the page's `GetByIdAsync` existence check returns null for a
  stale id; the page re-renders the list with the same not-found notice rather
  than throwing or returning a bare 404, keeping the admin on the working list
  page. The endpoint keeps its `Results.NotFound()`.

A dedicated `UserAdminService` type (Plan B's proposal) was considered and
rejected. `AuthFlows` (`src/Freeboard/Auth/AuthFlows.cs`) is already the
project's user/credential sharing seam: it is `internal static`, both
`UserAdminEndpoints` and the page handlers live in the same assembly, and it
already exposes exactly this shape - a result-record discriminated union plus a
static method per operation (`LoginAsync`/`LoginResult`,
`ResetPasswordAsync`/`PasswordResult`, `BootstrapAsync`/`BootstrapResult`, etc.).
Adding `CreateUserAsync`/`CreateUserResult` and
`ResetUserPasswordAsync`/`ResetUserPasswordResult` is a same-pattern extension of
an existing seam. A new service type would be a second sharing mechanism doing
the same job for a single caller-pair - new surface against code-as-liability,
and it would diverge from the one seam the rest of auth already uses.

`AuthFlows` is large (about 1080 lines), which is the one argument for splitting.
That is a general file-size concern, not specific to this change; splitting
`AuthFlows` by concern is out of scope here and would be a broad refactor with no
payoff for this feature. If the file later warrants splitting, the user-admin
methods move with the rest as one unit. For this change, the lower-liability move
is to follow the established seam.

This is a refactor of existing code (move the body of the private handlers into
shared methods), not net-new behavior, consistent with the code-as-liability
rule. The API behavior must stay identical; the endpoint tests
(`UserAdminEndpointTests`) guard that.

### Authentication via folder authorize; admin role in-page

Add `AuthorizeFolder("/Admin", PageChallengeScheme.PolicyName)` in `Program.cs`,
mirroring `/Account`. This gives the same unauthenticated-to-`/login` redirect
for free, before any handler runs.

Each admin page handler then performs the admin-role check in-page:
`User.FindFirst(AuthClaims.Role)?.Value == GlobalRoles.Admin`. When not admin,
the handler returns a bare HTTP 403 - a `StatusCodeResult(403)` (equivalently
`Results.StatusCode(403)`) - NOT `Forbid()`. A non-admin must not be able to
enumerate users or hit a mutating action, so the page model returns 403 before
reading or mutating any data. A single shared guard helper (checked at the top
of each `OnGet`/`OnPost`) keeps this consistent.

Rationale - and the reason `Forbid()` is wrong here: the `/Admin` folder is
authorized with `PageChallengeScheme.PolicyName`, so `Forbid()` resolves to that
scheme's `HandleForbiddenAsync`, which redirects to `/account/sudo`
(`src/Freeboard/Web/PageChallengeScheme.cs:42-46`). A non-admin would then be
sent to the sudo step-up page, misrepresenting an admin-role denial as a
missing-step-up. A bare `StatusCodeResult(403)` does not invoke any
authentication scheme, so it returns a clean 403 and keeps the admin gate
independent of the sudo mechanism. This matches the existing in-page gating
precedent (`Recovery.cshtml.cs` checks sudo recency in-handler and self-redirects
rather than leaning on the scheme).

Alternative considered: a second page scheme/policy whose forbid handler 403s
instead of redirecting. Rejected: it adds a scheme and a policy (new surface) to
express what one claim check in the handler already expresses, against
code-as-liability.

### Force-reset (limited) sessions cannot reach admin pages

A force-reset-limited session (a user mid-forced-reset) must not reach the admin
pages. No new code is needed: `LimitedSessionGuardMiddleware`
(`src/Freeboard/Auth/LimitedSessionGuardMiddleware.cs:36-67`) already blocks any
authenticated limited session on every route whose endpoint does NOT carry the
`LimitedSessionAllowed` marker, redirecting a matched page route to
`/account/complete-reset`. The marker is added in `Program.cs` only to
`/Account/CompleteReset`, `/Account/Index`, and `/Logout`
(`src/Freeboard/Program.cs:171-174`). The admin pages deliberately do NOT carry
the marker, so a limited session hitting any `/admin` page is funnelled to
`/account/complete-reset` before any admin handler runs - even before the in-page
admin-role check. This is the mechanism Plan B flagged (M-2); it is satisfied by
omission, so the only requirement is to NOT add `LimitedSessionAllowed` to any
admin page. A Unit test asserts a limited session GETting an admin page is
redirected to `/account/complete-reset`.

### Antiforgery on every POST

Reuse the existing global `AutoValidateAntiforgeryTokenAttribute` page
convention (already configured in `Program.cs`). Every admin POST form includes
the antiforgery hidden field; the convention rejects a POST without a matching
token + cookie with 400. No per-page attribute is needed. The single mutating
page `/Admin/Users` (which hosts all four create/disable/enable/reset-password
POST handlers) is added to the existing `AuthEndpoint` page-metadata loop in
`Program.cs` so `GitOpsReadOnlyMiddleware` exempts it from the read-only 409,
exactly as it exempts the API user-admin endpoints (those are mutating admin
actions that must work in read-only GitOps mode, same as the API). The marker is
applied via `options.Conventions.AddPageMetadata(page, new AuthEndpoint())`
(`Program.cs:178-189`), which marks the whole page endpoint, so the page-string
list gets exactly `"/Admin/Users"`. `/Admin/UserCredential` is GET-only and is
NOT added.

### One-time temp-password handoff reuses the display-store pattern

Add `TempPasswordDisplayStore` in `src/Freeboard/Web/`, a near-copy of
`RecoveryCodeDisplayStore` but holding a single string (the temp password,
optionally with the target user's email/id for the display heading) instead of a
list of codes. It uses `IMemoryCache` with a short TTL, a `StrongBox` +
`Interlocked.Exchange` claim, a `Stash` returning a nonce, a
`StashAndRedirectTarget(response, ...)` that sets the transient cookie and
returns the display route, and a `Take(nonce)` that reads-and-clears.

`RecoveryCodeDisplayStore` uses a single `DisplayPath` constant as both the
redirect route and the cookie `Path`. The temp-password store does the same with
the concrete value `"/admin/usercredential"`. Browser cookie paths are
case-sensitive, so the `DisplayPath` constant, the cookie `Path`, and the
redirect target must all be this exact lowercase string. To keep the route a
single source of truth, `UserCredential.cshtml` declares `@page
"/admin/usercredential"` explicitly, so the PascalCase file serves the lowercase
route - mirroring `RecoveryCodes.cshtml`, which declares `@page
"/account/mfa/recovery-codes"` for the same reason.

Add one transient cookie name to `SessionCookie` (e.g.
`AdminTempPasswordName`), path-scoped to that display path. The display page
(`Pages/Admin/UserCredential.cshtml`) runs the in-page admin guard, then reads
the nonce, calls `Take`, clears the cookie, and sets `Cache-Control: no-store` +
`Pragma: no-cache`, mirroring `RecoveryCodesModel`.

The create and reset POST handlers, after generating the temp password, call
`Stash`/`StashAndRedirectTarget` and redirect to the display page. The plaintext
never enters a URL, a form field, the log, or a persisted cookie; only the nonce
rides a short-lived path-scoped cookie. The known multi-instance non-sticky
caveat (the display can miss the entry and show nothing) is the same safe
failure documented on `RecoveryCodeDisplayStore`: the admin re-runs the action.

The display page runs the in-page admin-role guard BEFORE it calls `Take`, so a
non-admin holding the nonce cookie cannot consume the one-time entry: the guard
returns 403 before `Take` claims the value, and the value stays available for the
admin's own visit. A Unit test proves this ordering (see verification).

Alternative considered: TempData. Rejected: TempData would put the plaintext in
a (signed, but client-held) cookie or in session state, widening exposure; the
display-store keeps the plaintext server-side only, which the recovery-code
flow already established as the project pattern.

### Create credential handoff: temp password or email invite

The create form offers two credential-handoff modes via a server-rendered radio
choice; the page re-renders both states with no client reactivity:

- Temporary password (default, always available): the existing behavior - the
  shared `AuthFlows.CreateUserAsync` generates a one-time temp password, stores
  only its Argon2id hash, sets `force_password_reset`, and the page redirects to
  the one-time display page.
- Email invite: no plaintext is generated. The user is created with
  `force_password_reset` set and no password credential row, then a single-use
  set-password link is minted and emailed. The create page re-renders in place
  with an inline confirmation panel ("Invite sent to <email>, link expires in 7
  days") - no display-page redirect (there is nothing to display) and no
  list-flash. The confirmation states the link expiry window.

#### Email-configured gate (the real mechanism)

The invite option gates ONLY on `AuthEmailService` presence:
`serviceProvider.GetService<AuthEmailService>() is not null`
(`src/Freeboard/Auth/AuthFlows.cs:222-223`). `AuthEmailService` is registered
only when an `IEmailSender` exists, and `EmailRegistration.Add`
(`src/Freeboard/Email/EmailRegistration.cs:28-42`) registers an `IEmailSender`
only when `Email:Transport` is `log` or `smtp` - `none` (the default) registers
nothing. So the presence of `AuthEmailService` is the single source of truth for
"email is configured".

This is the email-presence half of `ForgotPasswordAsync`'s gate, NOT the same
gate. `ForgotPasswordAsync` gates on `PasswordResetEnabled && emailService`; the
invite intentionally does NOT honor `Auth:PasswordResetEnabled`. The divergence
is deliberate: the invite is an authenticated admin action, independent of the
public self-serve forgot-password toggle, so an admin can invite even when public
password reset is disabled. A consequence (already true in code): a minted invite
token is consumable via `/reset-password` even when `PasswordResetEnabled=false`.
That is intended for invites - the `/reset-password` page consumes a valid token
regardless of the public toggle.

When `AuthEmailService` is absent, the create page renders the invite radio
disabled with a short explanation ("Email is not configured; new users get a
temporary password") and the create handler defends server-side: an invite
request with no `AuthEmailService` falls back to the temp-password path rather
than failing. The gate is enforced in the handler, not just the markup, so a
forged POST cannot reach an invite send when email is off.

#### Invite token + accept flow: reuse, do not invent

The invite is a set-your-own-password link, which is exactly the existing
password-reset flow. Reuse it end to end:

- Token: `IPasswordResetStore.CreateAsync(userId, expiresAt)`
  (`src/Freeboard.Persistence/Auth/IPasswordResetStore.cs:20`) mints a
  prefix-bearing, keyed-HMAC-at-rest, single-use, expiry-bounded token. The invite
  uses an explicit 7-day expiry literal at the call site (`now + 7 days`), NOT
  `WebAuthOptions.PasswordResetLifetime` (1h, too short for an invite to reach an
  inbox and be acted on). `CreateAsync` already takes an arbitrary `expiresAt`, so
  this is a literal at the call site, not new config surface.
- Email: add one method `AuthEmailService.SendInviteAsync(email, token)` next to
  `SendPasswordResetAsync`/`SendMagicLinkAsync`
  (`src/Freeboard/Auth/AuthEmailService.cs`). It builds the link with the
  existing `BuildLink("/reset-password", token)` and a short invite body. It does
  NOT log the token (same rule the file already states).
- Accept page: the existing `/reset-password` page
  (`src/Freeboard/Pages/ResetPassword.cshtml.cs`) already scrubs the token from
  the URL into a transient cookie, renders the new-password form, and on POST
  (antiforgery-protected) calls `AuthFlows.ResetPasswordAsync`, which consumes
  the token single-use and clears `force_password_reset`
  (`src/Freeboard/Auth/AuthFlows.cs:249-278`). The invitee sets their password
  through this unchanged page. No new accept page or token store is added,
  consistent with code-as-liability.

#### What is stored on the invite path

User row with `force_password_reset = true` and NO password credential row
(`IPasswordCredentialStore.SetAsync` is NOT called on this path). The only
secret is the reset token, stored as its keyed HMAC by the existing store; the
plaintext token rides only the emailed link and the `/reset-password` transient
cookie, never a log, the create response, or a persisted cookie. Until the
invite is accepted the account cannot log in (no password) and any login attempt
fails closed.

#### Invite provisioning failure (token mint or email send): surface the error, recover via reset-password

The user row is created before both the token mint and the send. Token mint
(`IPasswordResetStore.CreateAsync`) and email send (`SendInviteAsync`) are one
invite-provisioning step: if EITHER throws (a token-store error, or an SMTP
outage), `CreateUserAsync` returns `InviteSendFailed(UserRow)`. Either way the
row exists with `force_password_reset` and no usable credential, so the outcome
and recovery are identical. The create page renders this as an error telling the
admin the user was created but the invite could not be provisioned, and that the
recovery is the per-user reset-password action on that user. Reset-password works
because the row exists and is enabled; it generates a temp password via the
existing one-time display path.

Re-running create does NOT work as recovery: the row already exists, so the
duplicate-email pre-check returns `DuplicateEmail`. The documented recovery is
reset-password.

Two deliberate non-goals (stated so a future reader knows the omission is
intentional):

- No temp-password auto-fallback on invite provisioning failure. The admin sees
  the error and chooses reset-password; create does not silently switch handoff
  modes.
- No re-send-invite action. There is one path back to a usable credential
  (reset-password), and it is enough.

#### Composition with force_password_reset

Both paths set `force_password_reset`. On the temp-password path the user logs in
with the temp password and is funnelled through `/account/complete-reset`. On the
invite path the user has no password, so they set one via `/reset-password`, which
clears `force_password_reset` directly - they never need the temp password or the
complete-reset funnel. The limited-session guard is unaffected.

#### Security

- Token entropy and at-rest protection are the existing store's: prefix-bearing,
  keyed-HMAC at rest, single-use (atomic conditional consume), expiry-bounded.
  No new token primitive.
- Antiforgery on the accept POST is the existing `/reset-password` page
  protection (global convention); the create POST is covered by the admin page
  antiforgery convention.
- No plaintext in logs or URLs: the temp password is not generated on this path;
  the token is never logged (existing rule), and the create response/redirect
  carries no secret (only an invite-sent confirmation).

#### Rejected alternatives

- Email the generated temporary password. Rejected: it puts a reusable plaintext
  credential in an inbox (long-lived, forwardable, indexable) and contradicts the
  existing "no emailing the temporary password" non-goal. The invite link is
  single-use, expiry-bounded, and sets a user-chosen password.
- A new invite-specific token store and accept page. Rejected: the password-reset
  store and `/reset-password` page already provide a single-use, expiry-bounded,
  set-your-own-password flow. A parallel mechanism is duplicate surface for the
  same job, against code-as-liability.

### Layout width for the user table

The shared `_Layout.cshtml` wraps content in `<main class="auth">` (styled by
`css/auth.css`), which is narrow and form-centric - fine for the auth screens and
for `UserCredential` (a single password block), but a multi-column user list +
create form can be cramped. Decision: reuse `_Layout` (do not fork it) but give
the users list page a wide content slot via an additional CSS class on its
top-level container (e.g. `class="admin-wide"`) plus a small rule in `auth.css`
that widens `max-width` for that class. This is a config/style change, not a new
layout, and keeps a single layout file. `UserCredential` keeps the default narrow
width.

### File changes

New files in `src/Freeboard`:

- `Pages/Admin/Users.cshtml` + `.cshtml.cs` - list + create form + per-user
  enable/disable/reset-password POST handlers.
- `Pages/Admin/UserCredential.cshtml` + `.cshtml.cs` - one-time temp-password
  display.
- `Web/TempPasswordDisplayStore.cs` - one-time display store.

Modified files in `src/Freeboard`:

- `Auth/AuthFlows.cs` (or equivalent shared seam) - add the shared create /
  reset helpers; the shared `CreateUserAsync` gains the email-invite branch
  (gate on `AuthEmailService`, mint a reset token, send the invite, no temp
  password); `Auth/UserAdminEndpoints.cs` - call them.
- `Auth/AuthEmailService.cs` - add `SendInviteAsync` (reuses the existing
  `/reset-password` link builder and `IEmailSender`).
- `Web/SessionCookie.cs` - one new transient cookie name.
- `Program.cs` - register `TempPasswordDisplayStore`,
  `AuthorizeFolder("/Admin", ...)`, and add the admin POST pages to the
  `AuthEndpoint` metadata list.

New test files:

- `tests/Freeboard.Web.Tests/AdminUserPagesTests.cs` - Unit (no Category trait;
  the Unit tier is `Category!=Integration&Category!=E2E&Category!=NFR`).
- `tests/Freeboard.Web.Tests/TempPasswordDisplayStoreTests.cs` - Unit.
- `tests/Freeboard.WebE2E/AdminUserPagesE2ETests.cs` - E2E
  (`[Trait("Category", TestCategories.E2E)]`, `[RequiresEnvVarFact]`).

This stays inside `src/Freeboard` and the test projects. No file is added to or
moved into `src/Freeboard.Enterprise`; the one-way EE rule and the reference
graph are unaffected. Agent and CLI are untouched and stay EE-free and
cross-platform.

### Verification strategy

- Unit (Web.Tests, via `AuthWebFactory`/`WebApplicationFactory`, in-memory
  fakes, no MySQL):
  - unauthenticated GET to an `/admin` page redirects to `/login`.
  - a force-reset-limited session GETting an `/admin` page is redirected to
    `/account/complete-reset` (the limited-session guard, not the admin check).
  - authenticated non-admin GET and POST return 403, with no store mutation. The
    non-admin POST carries a valid antiforgery token, so the 403 proves the
    admin-role check denies it (not antiforgery).
  - authenticated admin GET renders the seeded users.
  - admin create: a user is added with `force_password_reset` set, only the hash
    is stored, the response redirects to the display page, and the display page
    shows the temp password once; a refresh shows nothing. The displayed
    plaintext verifies against the stored credential hash via
    `IPasswordHasher.Verify`, proving the value shown is the value that was hashed
    (a bug that hashed one value and displayed another would otherwise pass a
    display-once check).
  - admin create with email invite (email configured): the user is created with
    `force_password_reset` set and NO password credential row, an invite is sent,
    the page shows the invite-sent confirmation, and NO temp password is shown
    (no redirect to the display page). A fake `IEmailSender` captures the
    message; assert a `/reset-password?token=` link was sent and the token
    consumes via `AuthFlows.ResetPasswordAsync` (proving it is a real,
    single-use, force-reset-clearing token).
  - invite option gated off when email is unconfigured: with no
    `AuthEmailService` registered, the create page renders the invite radio
    disabled, and a forged create POST requesting the invite path falls back to
    the temp-password path (a temp password is shown, a credential hash is
    stored) rather than sending or erroring.
  - invite token is single-use and expires: consuming the invite token a second
    time fails; an expired token fails (reuse the existing password-reset store
    tests' approach).
  - invite ignores `Auth:PasswordResetEnabled`: with `AuthEmailService` registered
    AND `Auth:PasswordResetEnabled=false`, an admin create-with-invite still
    succeeds, mints a token, and the emitted `/reset-password` link is consumable
    (the new user can set a password). Guards against a regression that copies
    `ForgotPasswordAsync`'s gate, which DOES honor `PasswordResetEnabled`.
  - admin reset-password: temp password shown once and verifies against the
    stored hash via `IPasswordHasher.Verify`, `force_password_reset` set, sessions
    revoked.
  - admin disable revokes sessions; enable clears disabled.
  - reset / enable / disable against an unknown (stale) id re-render the list with
    a not-found notice and make no change.
  - duplicate email re-renders the create error and adds no user.
  - a non-admin cannot consume the one-time display nonce: stash a temp password,
    GET `/admin/usercredential` as a non-admin WITH the nonce cookie and assert
    403, then GET as admin and confirm the password is still available - proving
    the admin check runs BEFORE `Take`.
  - POST without an antiforgery token returns 400 (reuse
    `AuthFormTestHelpers.PostFormAsync` for the valid-token path and a raw POST
    for the missing-token path, as `PageAuthRedirectTests` does).
  - GitOps read-only mode (factory `ReadOnly = true`): an admin POST against a
    seeded user (e.g. disable, then enable) is not 409'd AND its effect lands
    (e.g. `Enabled` flips). `RouteMoveReadOnlyTests` is the precedent for the
    `AuthEndpoint` read-only-exemption pattern (it asserts a marked mutating route
    returns a non-409 status), not for asserting the effect; this test goes
    further and asserts the effect, because a create POST with an empty/duplicate
    body would be non-409 without proving the action ran, so use a seeded
    enable/disable as the proof.
  - `TempPasswordDisplayStore`: stash/take is one-time and concurrency-safe
    (mirror `RecoveryCodeDisplayStoreTests`).
  - the temp-password plaintext never appears in the captured logs
    (`AuthWebFactory.Logs`), in the create/reset redirect `Location` header, or in
    any `Set-Cookie` value.
- E2E (WebE2E, Playwright, gated): an admin logs in, creates a user, sees the
  temp password once, and the new user appears in the list; a refresh of the
  display page shows nothing. The test then logs in as the new user with the
  displayed temp password and completes the forced reset, proving the displayed
  value is the credential that was set (not just that some string rendered).
- E2E invite happy path (gated on BOTH `FREEBOARD_TEST_E2E` and
  `FREEBOARD_TEST_SMTP`, against a local Mailpit): with email configured, an
  admin creates a user via the invite option, the page shows the invite-sent
  confirmation (no temp password), the test reads the invite link from Mailpit's
  HTTP API, opens `/reset-password`, sets a password, and logs in as the new
  user - proving the invite link sets a working credential and clears the
  forced-reset flag. Skips cleanly when either gate is unset.
- `dotnet build` and `dotnet test` (Unit tier) must pass. E2E runs only when
  `FREEBOARD_TEST_E2E` and a Chromium are present.

## Risks / Trade-offs

- [Refactor of `UserAdminEndpoints` into shared helpers could change API
  behavior] -> Keep the move mechanical; the existing `UserAdminEndpointTests`
  must still pass unchanged, and assert the same response shapes.
- [In-page admin check is per-handler, so a new admin page that forgets the
  check would leak] -> Put the check in one shared guard helper called at the top
  of every admin handler, and add a Unit test that a non-admin gets 403 on each
  admin route. A future page added without the guard is caught by extending that
  test.
- [Multi-instance non-sticky deployment can miss the display-store entry on the
  redirect] -> Same safe failure as the recovery-code store: the page shows
  nothing and the admin re-runs the action. Documented, not a data leak.
- [Temp-password plaintext exposure] -> Server-side-only store, opaque nonce
  cookie, `no-store` on the display page, and a test asserting the plaintext is
  absent from logs and cookies.
- [Invite provisioning fails (token-store error or SMTP outage) after the user
  row is created] -> Unlike the enumeration-safe forgot-password flow, this is an
  authenticated admin action, so a failure in EITHER the token mint or the email
  send surfaces to the admin as `InviteSendFailed`: the create page reports the
  user was created but the invite could not be provisioned. The recovery is the
  per-user reset-password action (re-create does NOT work - the row exists, so the
  duplicate-email pre-check blocks it). No temp-password auto-fallback and no
  re-send-invite action (both deliberate non-goals). The user row exists with
  `force_password_reset` set and no password, so it cannot log in until reset
  succeeds. The token is never logged on this path.
- [Forged invite POST when email is off] -> The gate is enforced in the handler
  (`AuthEmailService` presence), not just the markup, so an invite request with
  no email configured falls back to the temp-password path rather than sending.

## Resolved questions

- Shared logic in `AuthFlows` or a new user-admin service type? Resolved:
  `AuthFlows`. See the "Pages over the store/flow layer" decision. `AuthFlows` is
  the established seam with the exact result-record-plus-static-method shape this
  needs; a new service type is a second mechanism for one caller-pair. Its size
  (about 1080 lines) is a general concern, not a reason to add a parallel seam.
- Sudo step-up for admin pages? Resolved: no. The API user-admin endpoints
  (`UserAdminEndpoints`) require only the admin policy, not sudo recency
  (`src/Freeboard/Auth/UserAdminEndpoints.cs:19`). Interface parity argues the
  browser pages match that: authenticated admin session is sufficient, no sudo
  gate. Adding a sudo gate to the pages but not the API would be inconsistent and
  is out of scope. (Contrast: the MFA-management pages are sudo-gated because
  their API counterparts are.)

## Sources and divergence resolution

This change merges two independent plans: Plan A (the OpenSpec artifacts already
written here) and Plan B (an alternative architecture sketch). Both reached the
same core design: SSR pages over the existing store/flow layer (not HTTP),
in-page admin-role authorization, antiforgery on every POST via the existing
convention, `AuthEndpoint` metadata for GitOps read-only parity, and a one-time
temp-password handoff copying the recovery-code display-store pattern.

Where they diverged, and how each was resolved:

1. Non-admin response. Plan A's prose said `Forbid()` while also noting the page
   scheme redirects a forbid to `/account/sudo` - self-contradictory. Plan B said
   a bare 403. Resolved to bare 403 (`StatusCodeResult(403)`). Evidence:
   `PageChallengeScheme.HandleForbiddenAsync` redirects to `/account/sudo`
   (`src/Freeboard/Web/PageChallengeScheme.cs:42-46`), and the `/Admin` folder is
   authorized with that scheme's policy, so `Forbid()` would send a non-admin to
   sudo. A bare status result invokes no scheme. (From Plan B; Plan A's own
   evidence confirmed it.)

2. Shared-layer location. Plan A: `AuthFlows`. Plan B: a dedicated
   `UserAdminService`. Resolved to `AuthFlows` (Plan A). Evidence: `AuthFlows` is
   `internal static`, about 1080 lines, and already uses the
   result-record-plus-static-method shape per operation
   (`src/Freeboard/Auth/AuthFlows.cs`). A new service is a second seam for one
   caller-pair. Rationale documented in the decision above.

3. Unit-test tagging. Plan A: no `Category` trait (the Unit tier filter is
   `Category!=Integration&Category!=E2E&Category!=NFR`). Plan B: tag
   `Category=Unit`. Resolved to no trait (Plan A). Evidence: the CI unit job runs
   `--filter "Category!=Integration&Category!=E2E&Category!=NFR"`
   (`.github/workflows/test.yml`), and no existing test carries `Category=Unit` -
   the project convention is that fast tests are untagged and selected by
   exclusion (e.g. `tests/Freeboard.Web.Tests/PageAuthRedirectTests.cs`,
   `RecoveryCodeDisplayStoreTests.cs`). `Category=Unit` would still run in that
   tier but breaks the convention. "Tagged by Category" is satisfied as: E2E
   tests carry `[Trait("Category", TestCategories.E2E)]`; Unit tests are
   deliberately untagged and selected by the exclusion filter.

4. Force-reset-limited sessions blocked from admin pages. Plan B raised it (M-2);
   Plan A did not call it out. Resolved: no new code, omit the
   `LimitedSessionAllowed` marker on admin pages so
   `LimitedSessionGuardMiddleware` auto-redirects a limited session to
   `/account/complete-reset`
   (`src/Freeboard/Auth/LimitedSessionGuardMiddleware.cs:36-67`,
   `src/Freeboard/Program.cs:171-174`). Documented as its own decision above and
   covered by a Unit test.

Other Plan B ideas folded in: an explicit Unit test that a non-admin POST with a
valid antiforgery token still gets 403 (proves the admin check, not antiforgery,
is doing the denying), and a test asserting the plaintext is absent from the
redirect `Location` and any `Set-Cookie`. Plan B's separate `/admin/users/create`
and `/admin/users/temp-password` routes were not adopted; Plan A's single
`Admin/Users` page (list + create form + per-user action POST handlers) plus one
`Admin/UserCredential` display page is fewer routes for the same capability.
