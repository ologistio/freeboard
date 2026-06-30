## ADDED Requirements

### Requirement: Admin user-management pages require an authenticated admin in-page

The web app SHALL provide server-rendered admin pages under `/admin` for user
management. Every admin page GET and POST handler SHALL require an authenticated
session AND the admin global role, enforced inside the page handler.

The authentication gate reuses the existing page funnel: the `/Admin` folder is
authorized with the same named page policy as `/account`, so an unauthenticated
browser is redirected to `/login` before any handler runs. The admin-role gate
is performed inside each handler by reading the role claim. The handler SHALL
return a bare HTTP 403 status result for a non-admin, NOT a framework
forbid: the page authorization scheme converts a forbid into a redirect to the
sudo page, which would misrepresent an admin-role denial as a missing step-up.
An authenticated non-admin SHALL NOT see any user data or reach any mutating
handler; the handler SHALL return 403 and make no change, even when the request
carries a valid antiforgery token.

A force-reset-limited session SHALL NOT reach any admin page. The admin pages do
not carry the limited-session allowlist marker, so the existing limited-session
guard redirects such a session to the forced-reset completion page before any
admin handler runs.

The pages SHALL be rendered server-side (no client-side reactivity or SPA) and
SHALL call the same store/flow layer the JSON `UserAdminEndpoints` use; they
SHALL NOT call the JSON API over HTTP and SHALL NOT duplicate its validation or
credential-handoff logic.

#### Scenario: Unauthenticated browser is redirected to login

- **WHEN** a request with no valid session cookie GETs an `/admin` page
- **THEN** the response is a redirect to `/login` and no user data is rendered

#### Scenario: Authenticated non-admin is forbidden

- **WHEN** an authenticated member (non-admin) GETs or POSTs any `/admin` page,
  including a POST that carries a valid antiforgery token
- **THEN** the response is a bare `403` (not a redirect to the sudo page), no
  user list or user data is rendered, and no change is made

#### Scenario: Force-reset-limited session is funnelled to complete the reset

- **WHEN** an authenticated session that is force-reset-limited GETs any `/admin`
  page
- **THEN** the response is a redirect to the forced-reset completion page and no
  admin handler runs

#### Scenario: Authenticated admin can view the user list

- **WHEN** an authenticated admin GETs the admin users page
- **THEN** the response is `200` and renders the current users from the store

### Requirement: Admin pages create, enable, disable, and reset-password

The admin pages SHALL expose the same user-management actions as
`UserAdminEndpoints`: list users, create a user, disable a user, enable a user,
and reset a user's password. Each mutating action SHALL be a POST handler. The
reset-password action SHALL produce a cryptographically random one-time
temporary password, store only its Argon2id hash, set the user's
`force_password_reset`, and revoke that user's sessions, exactly as the API
endpoints do.

The create action SHALL offer a credential-handoff choice:

- Temporary password (default): same as reset-password - a one-time temporary
  password is generated, only its Argon2id hash is stored, `force_password_reset`
  is set, and the password is shown to the admin once. No email is sent.
- Email invite: available only when auth email is configured. The user SHALL be
  created with `force_password_reset` set and NO password credential; a
  single-use set-your-own-password link with a 7-day expiry SHALL be emailed to
  the new user, reusing the existing password-reset token store and
  `/reset-password` page. No temporary password SHALL be generated, stored, or
  shown on this path. The invite-sent confirmation SHALL be rendered in-page and
  SHALL state the link expiry window.

The email-invite option SHALL be gated on auth email being configured at runtime
(presence of the auth email service). It SHALL NOT depend on the public
self-serve password-reset toggle: an admin SHALL be able to invite even when
public password reset is disabled. When auth email is not configured the option
SHALL be unavailable (hidden or disabled with an explanation) and create SHALL
use the temp-password path even if an invite is requested.

If invite provisioning fails after the user row is created - whether the
single-use token mint or the email send fails - the create page SHALL report that
the user was created but the invite could not be provisioned, and SHALL direct
the admin to the per-user reset-password action as the recovery. The system SHALL
NOT auto-fall back to a temporary password and SHALL NOT offer a re-send-invite
action.

Validation and conflict handling SHALL match the API behavior: a missing email
or name, an unknown role, or a duplicate normalized email SHALL re-render the
create form with the error and SHALL NOT create a user.

#### Scenario: Admin creates a user

- **WHEN** an admin submits the create form with a new email, name, and role
- **THEN** a user is created with `force_password_reset` set, only the Argon2id
  hash of the temporary password is stored, and the admin is shown the temporary
  password once

#### Scenario: Admin creates a user with an email invite when email is configured

- **WHEN** auth email is configured and an admin submits the create form with a
  new email, name, and role and chooses the email-invite handoff
- **THEN** the user is created with `force_password_reset` set and no password
  credential, a single-use set-your-own-password link is emailed to the new
  user, the admin sees an invite-sent confirmation, and no temporary password is
  generated or shown

#### Scenario: Email-invite option is unavailable when email is not configured

- **WHEN** auth email is not configured and the admin opens the create form
- **THEN** the email-invite option is hidden or disabled with an explanation, and
  a create request that asks for the invite path falls back to generating a
  one-time temporary password (no email is sent and no invite link is minted)

#### Scenario: Invite link lets the new user set a password and clears the forced reset

- **WHEN** the invited user opens the emailed set-your-own-password link and
  submits a new password
- **THEN** the password is set, `force_password_reset` is cleared, and the user
  can log in - reusing the existing password-reset page and flow

#### Scenario: Invite token is single-use and expires

- **WHEN** the invite token is presented a second time, or after its expiry
- **THEN** it is rejected and no password change is made (the token is single-use
  and expiry-bounded by the existing password-reset token store)

#### Scenario: Invite provisioning failure surfaces and recovers via reset-password

- **WHEN** an admin chooses the email-invite handoff and invite provisioning fails
  after the user row has been created - either the single-use token mint or the
  email send throws
- **THEN** the user row exists with `force_password_reset` set and no usable
  credential, the create page reports the user was created but the invite could
  not be provisioned, and the admin is directed to the per-user reset-password
  action as the recovery - with no temporary-password auto-fallback and no
  re-send-invite action

#### Scenario: Invite is allowed when public password reset is disabled

- **WHEN** auth email is configured but the public self-serve password-reset
  toggle is off, and an admin submits the create form choosing the email-invite
  handoff
- **THEN** the invite is sent and the minted link is consumable via
  `/reset-password`, because the invite gates only on auth email being configured

#### Scenario: Create with a duplicate email re-renders the error

- **WHEN** an admin submits the create form with an email that already exists
  (normalized)
- **THEN** no user is created and the form re-renders with a validation error

#### Scenario: Admin disables a user

- **WHEN** an admin posts the disable action for an enabled user
- **THEN** the account is disabled and that user's sessions are revoked

#### Scenario: Admin enables a user

- **WHEN** an admin posts the enable action for a disabled user
- **THEN** the account is enabled

#### Scenario: Admin resets a user's password

- **WHEN** an admin posts the reset-password action for a user
- **THEN** a fresh temporary password is generated and shown once, the user's
  `force_password_reset` is set, and that user's sessions are revoked

#### Scenario: Action against a stale (deleted) user id is handled

- **WHEN** an admin posts a disable, enable, or reset-password action for a user
  id that no longer exists (the user was removed between the list render and the
  POST)
- **THEN** no change is made and the list page re-renders with a not-found notice
  rather than an error page

### Requirement: Every admin page POST validates an antiforgery token

Every admin page POST handler SHALL require a valid antiforgery token. A POST
without a matching antiforgery token and cookie SHALL be rejected before the
handler mutates any state. This reuses the global Razor Pages antiforgery
convention already applied to the auth screens; the admin POST pages SHALL be
marked so GitOps read-only mode exempts them from the read-only conflict exactly
as it exempts the API user-admin endpoints.

#### Scenario: POST without an antiforgery token is rejected

- **WHEN** a request POSTs an admin action form without a valid antiforgery
  token and cookie
- **THEN** the response is `400` and no change is made

#### Scenario: Admin POST is allowed in GitOps read-only mode

- **WHEN** the app runs in GitOps read-only mode and an admin posts a valid
  admin action
- **THEN** the action is not blocked by the read-only conflict (it is treated
  like the API user-admin endpoints)

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

#### Scenario: A non-admin cannot consume the one-time display nonce

- **WHEN** a temporary password is stashed and a non-admin requests the display
  page carrying the nonce cookie
- **THEN** the response is `403`, the entry is NOT consumed, and a subsequent
  request by an admin still shows the temporary password (the admin-role check
  runs before the entry is claimed)
