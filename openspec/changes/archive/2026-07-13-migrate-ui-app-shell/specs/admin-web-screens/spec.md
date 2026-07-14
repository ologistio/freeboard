## MODIFIED Requirements

### Requirement: Admin user-management pages require an authenticated admin in-page

The web app SHALL provide server-rendered user-management pages served under
`/settings` (the users page at `/settings/users` and the one-time temporary-password
display at `/settings/usercredential`). Every such page GET and POST handler SHALL
require an authenticated session AND the `user.manage` permission, enforced inside the
page handler.

The authentication gate reuses the existing page funnel: the page files remain in the
`Pages/Admin` Razor Pages folder, so the `/Admin` folder is authorized with the same
named page policy as `/account`, and an unauthenticated browser is redirected to
`/login` before any handler runs - the route URL moving under `/settings` does not
change this, because the folder authorization is keyed on the page file path, not the
URL. The permission gate is performed inside each handler by `AuthzPageGuard`, which
checks the `user.manage` action; the legacy `freeboard:role=admin` claim alone grants
nothing. The handler SHALL return a bare HTTP 403 status result when the permission
check fails, NOT a framework forbid: the page authorization scheme converts a forbid
into a redirect to the sudo page, which would misrepresent a permission denial as a
missing step-up. An authenticated user without `user.manage` SHALL NOT see any user
data or reach any mutating handler; the handler SHALL return 403 and make no change,
even when the request carries a valid antiforgery token.

A force-reset-limited session SHALL NOT reach any user-management page. The pages do
not carry the limited-session allowlist marker, so the existing limited-session guard
redirects such a session to the forced-reset completion page before any handler runs.

The pages SHALL be rendered server-side (no client-side reactivity or SPA) and SHALL
call the same store/flow layer the JSON `UserAdminEndpoints` use; they SHALL NOT call
the JSON API over HTTP and SHALL NOT duplicate its validation or credential-handoff
logic. The prior `/admin/users` and `/admin/usercredential` URLs are retired with no
redirect (a deliberate clean break in pre-release software).

#### Scenario: Unauthenticated browser is redirected to login

- **WHEN** a request with no valid session cookie GETs a user-management page (for
  example `/settings/users`)
- **THEN** the response is a redirect to `/login` and no user data is rendered

#### Scenario: Authenticated user without user.manage is forbidden

- **WHEN** an authenticated user who lacks the `user.manage` permission GETs or POSTs
  any user-management page, including a POST that carries a valid antiforgery token
- **THEN** the response is a bare `403` (not a redirect to the sudo page), no
  user list or user data is rendered, and no change is made

#### Scenario: Force-reset-limited session is funnelled to complete the reset

- **WHEN** an authenticated session that is force-reset-limited GETs any
  user-management page
- **THEN** the response is a redirect to the forced-reset completion page and no
  handler runs

#### Scenario: Authenticated user with user.manage can view the user list

- **WHEN** an authenticated user holding the `user.manage` permission GETs
  `/settings/users`
- **THEN** the response is `200` and renders the current users from the store

### Requirement: One-time temporary-password handoff

The temporary password SHALL be shown to the admin exactly once and never
persisted in a client-readable form. This applies to reset-password and to
create on the temp-password path; the email-invite create path produces no
temporary password, so this handoff does not apply to it. The
plaintext SHALL NOT appear in a URL, a client-readable field, the request log,
or any persisted cookie. The handoff SHALL reuse the recovery-code display-store
pattern: the generating handler stashes the plaintext server-side keyed by an
opaque nonce with a short TTL, sets only a path-scoped transient nonce cookie,
and redirects to a display page that reads-and-clears the entry. A refresh or a
later visit SHALL find nothing.

The temporary password SHALL be stored only as its Argon2id hash; the plaintext
SHALL exist only in the single display.

#### Scenario: Temporary password shows exactly once

- **WHEN** an admin creates or resets a user and is redirected to the display
  page
- **THEN** the temporary password renders once

#### Scenario: Refresh of the display page shows nothing

- **WHEN** the admin refreshes or revisits the display page after the first view
- **THEN** the temporary password is not shown and no new value is generated

#### Scenario: Plaintext never reaches a URL, log, or persisted cookie

- **WHEN** a temporary password is generated and handed off
- **THEN** the plaintext does not appear in any URL, the request log, or a
  client-readable or persisted cookie; only an opaque nonce rides a short-lived
  cookie

#### Scenario: A user without user.manage cannot consume the one-time display nonce

- **WHEN** a temporary password is stashed and a user without the `user.manage`
  permission requests the display page carrying the nonce cookie
- **THEN** the response is `403`, the entry is NOT consumed, and a subsequent
  request by a user holding `user.manage` still shows the temporary password (the
  AuthzPageGuard `user.manage` check runs before the nonce is claimed)
