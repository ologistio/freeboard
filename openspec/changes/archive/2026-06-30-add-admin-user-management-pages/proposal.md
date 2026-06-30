## Why

The web app has admin user-management endpoints (`UserAdminEndpoints`:
list/create/enable/disable/reset-password) and a browser session funnel (the
auth web screens under `Pages/`), but no browser UI to manage users. An admin
must use the CLI or raw API calls. This change adds server-rendered admin pages
so an admin can list, create, enable, disable, and reset-password for users from
the browser, using the session cookie they already hold.

## What Changes

- Add Razor Pages under `src/Freeboard/Pages/Admin/` that render and drive the
  existing user-management behavior:
  - `Admin/Users` (GET) - list users, with create form and per-user
    enable/disable/reset-password actions.
  - `Admin/Users` create (POST handler) - create a user. The create form offers
    a credential-handoff choice:
    - Temporary password (default/fallback): generate a one-time temp password
      and show it once on the display page (the existing behavior).
    - Email invite: mint a single-use set-your-own-password link and email it to
      the new user; no plaintext temp password is generated, stored, or shown for
      this path. The new user is still created with `force_password_reset` set and
      no usable password until they set one via the link.
  - per-user disable / enable / reset-password (POST handlers).
  - `Admin/UserCredential` (GET) - one-time display of the generated temporary
    password after a create (temp-password path) or reset, then it is gone. On
    the email-invite path there is no temp password, so create re-renders the page
    in place with an inline invite-sent confirmation (stating the 7-day link
    expiry) instead of redirecting to this page. If the invite send fails, create
    re-renders an error and the admin recovers via the per-user reset-password
    action.
- Gate the email-invite option on whether auth email is configured at runtime
  (an `AuthEmailService` is registered, which happens only when
  `Email:Transport` is not `none`). When email is not configured the option is
  hidden/disabled with a short explanation and create uses the temp-password
  path. The invite path reuses the existing password-reset token store and the
  existing `/reset-password` set-password page; no new token store or accept
  page is added.
- Reuse the existing `AuthFlows`/store layer the API endpoints already call; the
  pages do not duplicate the credential-handoff or validation logic.
- Authorize the admin role in-page in every handler. The Razor Pages folder
  authorize convention only proves authentication (matching the `/account`
  folder); the admin-role check runs inside each page handler, because a folder
  authorize policy that forbids a non-admin would redirect to the sudo page (the
  existing page forbid fallback), which is wrong for an admin gate.
- Antiforgery on every POST handler via the existing global
  `AutoValidateAntiforgeryTokenAttribute` page convention; the single mutating
  page `/Admin/Users` (it hosts the create/disable/enable/reset-password POST
  handlers) is added to the `AuthEndpoint` page-metadata list so GitOps read-only
  mode treats it the same as the API user-admin endpoints. `/Admin/UserCredential`
  is GET-only and is NOT added.
- One-time temporary-password handoff reuses the recovery-code display-store
  pattern: a new in-memory display store stashes the temp password keyed by a
  nonce, sets a path-scoped transient cookie, and the display page reads-and-
  clears it so the plaintext shows exactly once and never rides a URL, a
  client-readable field, or a persisted cookie.
- Tests: Unit (web) coverage for in-page authorization, antiforgery, and the
  one-time handoff; browser E2E coverage for the admin happy path. Test tiering
  follows the project convention: Unit tests are deliberately untagged (the CI
  Unit job selects them by the exclusion filter
  `Category!=Integration&Category!=E2E&Category!=NFR`); only the E2E tests carry
  `Category=E2E`.

This change is MIT (default). All code lands in `src/Freeboard` (the web app,
which is MIT) and the test projects. No code is added to or moved into
`src/Freeboard.Enterprise`; user management is a base capability, not a paid
enterprise carve-out.

## Non-goals

- No behavioral or API-contract change to `UserAdminEndpoints`, `AuthFlows`, or
  the stores. The create/reset bodies are refactored into shared `AuthFlows`
  helpers, but the JSON contract, status codes, and observable behavior stay
  identical. The pages call the same store/flow layer; they do not call the API
  over HTTP.
- No new user-management capability (no roles editor, no bulk actions, no
  delete, no audit view). Fine-grained RBAC stays a non-goal, as in
  `GlobalRoles`.
- No emailing of the generated temporary password. The temp-password path stays
  in-band and one-time, as the API already does. The email-invite path is
  different: it does NOT email a temp password - it emails a single-use
  set-your-own-password link and never generates plaintext, so this non-goal and
  the invite option do not conflict.
- No new token format, token store, or set-password page for the invite. The
  invite reuses the existing password-reset token store and the existing
  `/reset-password` page unchanged.
- No client-side reactivity or SPA. Server-side rendering only, consistent with
  the existing auth screens.
- No second session store, token format, or authorization mechanism. Reuse the
  cookie bridge and bearer handler unchanged.

## Capabilities

### New Capabilities

- `admin-web-screens`: Server-rendered admin pages for user management
  (list/create/enable/disable/reset-password), in-page admin-role authorization,
  antiforgery on every POST, a one-time temporary-password display, and a
  create-time credential-handoff choice between a temporary password and an
  email invite link gated on whether auth email is configured.

### Modified Capabilities

<!-- None. UserAdminEndpoints and its user-admin spec are unchanged. -->

## Impact

- `src/Freeboard/Pages/Admin/` - new pages and page models.
- `src/Freeboard/Web/` - new one-time temp-password display store (mirrors
  `RecoveryCodeDisplayStore`).
- `src/Freeboard/Auth/AuthEmailService.cs` - one new `SendInviteAsync` building
  the invite body over the existing `/reset-password` link (reuses the existing
  link builder and `IEmailSender`).
- `src/Freeboard/Auth/AuthFlows.cs` - the shared `CreateUserAsync` gains an
  invite branch that mints a reset token (existing `IPasswordResetStore`) and
  sends the invite instead of generating a temp password.
- `src/Freeboard/Web/SessionCookie.cs` - one new transient cookie name.
- `src/Freeboard/Program.cs` - register the display store, authorize the
  `/Admin` folder for authentication, add the single mutating page `/Admin/Users`
  to the `AuthEndpoint` metadata list (`/Admin/UserCredential` is GET-only and not
  added).
- `tests/Freeboard.Web.Tests/` - new Unit tests.
- `tests/Freeboard.WebE2E/` - new E2E tests.
- No new external dependency. No database or migration change.
