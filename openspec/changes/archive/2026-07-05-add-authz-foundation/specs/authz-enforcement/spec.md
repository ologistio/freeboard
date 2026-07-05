## ADDED Requirements

### Requirement: The app calls one authorizer seam that fails closed

The web app SHALL enforce authorization through a single `IAuthorizer` seam that
takes the request principal, an action, and a resource reference, and resolves a
`Permit` or `Deny` by loading the principal's authorization facts, resolving the
resource organisation's ancestry, and evaluating the core engine. Any failure to
reach a decision (missing data, thrown resolver, unreachable store) SHALL resolve
to `Deny`.

#### Scenario: Denied action is refused

- **WHEN** the authorizer evaluates an action the principal is not permitted
- **THEN** it returns `Deny` and the endpoint refuses the request

#### Scenario: Failure resolves to deny

- **WHEN** the authorizer cannot reach a decision because a dependency throws
- **THEN** it resolves to `Deny` rather than allowing the request

### Requirement: Mutating endpoints opt in to a permission check

Mutating minimal-API endpoints SHALL opt in to authorization through a
`RequirePermission(action, resourceSelector)` endpoint filter that extracts the
resource and its organisation from the route or body and calls the authorizer. Once
an endpoint opts in, the engine's default-deny and the filter's deny-on-missing/
throwing-selector make an incomplete check fail closed. A route that does NOT carry
the filter, however, still runs its handler untouched: runtime default-deny only
applies when the filter is invoked, so a forgotten filter leaks rather than denies.
The guard against a forgotten filter SHALL therefore be an automated metadata/
architecture test that inspects route metadata and asserts every mutating
compliance, role-assignment, and user-admin route carries BOTH a permission
requirement AND `alwaysEnforce: true` - not runtime behaviour. The `alwaysEnforce`
assertion is required because the rollout mode relaxation governs reads only and a
mutating route wired with `alwaysEnforce: false` would be silently mode-relaxed
rather than force-enforced, leaking a write the same way a missing filter would. Sensitive Razor Page handlers SHALL call the authorizer
directly through a page guard, because pipeline authorization does not run for
in-process page handlers. The page guard SHALL replace the previous admin page
guard.

#### Scenario: Permitted caller passes the filter

- **WHEN** an `org-owner` of O calls a `compliance.scope.write` endpoint for a
  resource in O's subtree
- **THEN** the filter permits the call and the handler runs

#### Scenario: Unpermitted caller is stopped by the filter

- **WHEN** a caller without the required permission on the target organisation (and
  not a super-admin) calls the write endpoint
- **THEN** the filter short-circuits before the handler and the store is unchanged

#### Scenario: Every mutating compliance, authz, and user-admin route carries a permission

- **WHEN** the mutating compliance, role-assignment, and user-admin routes are
  inspected
- **THEN** each carries both a permission requirement and `alwaysEnforce: true`,
  verified by an automated metadata test (the guard against a forgotten or
  mode-relaxed filter, since an ungated or `alwaysEnforce: false` route would still
  run or be mode-relaxed)

### Requirement: Cross-user session routes require user.manage; own-session access stays self-only

Cross-user session access SHALL require an authorizer `user.manage` decision, while
a caller's own-session access stays self-only. This covers the session routes
`GET`/`DELETE /auth/sessions/{id}` and `GET`/`DELETE /users/{id}/sessions`, which
serve two paths: a caller acting on its OWN sessions, and a caller acting on
ANOTHER user's sessions. A caller acting on its own sessions SHALL be permitted
self-only, by the existing owner-id match, with no permission required and no change
to current behavior. A caller acting on another
user's sessions SHALL be permitted ONLY if the authorizer permits `user.manage`
(reachable only through `system.admin`) on the target `user` resource. This
cross-user authorization SHALL replace the previous derivation from the legacy
`freeboard:role=admin` claim, so the legacy admin claim SHALL NOT grant any
cross-user session access: a caller holding only the legacy admin claim, without a
`user.manage` (or `system.admin`) grant, SHALL NOT list or revoke another user's
sessions. The cross-user check SHALL be force-enforced in every rollout mode
(`Observe`, `Compat`, `Enforce`) - it is a privileged cross-user read or mutation,
not an org-scoped compliance read, so the read-only mode relaxation and the Compat
read fallback SHALL NOT apply to it - and the decision SHALL be audited. Each such
route SHALL carry cross-user permission metadata so the route-metadata architecture
test can assert the required permission is DECLARED; because the gate is in-handler
(not a route filter), that metadata assertion proves only that the declaration is
present and SHALL NOT be relied on to prove the in-handler check runs. Behavioral web
tests SHALL guarantee the in-handler enforcement.

#### Scenario: Legacy admin claim cannot touch another user's sessions

- **WHEN** a caller holding only the legacy `freeboard:role=admin` claim, with no
  `user.manage` or `system.admin` grant, requests `GET`/`DELETE /users/{id}/sessions`
  or `GET`/`DELETE /auth/sessions/{id}` for a session it does not own
- **THEN** the request is refused (the target's existence is not disclosed) and no
  session is listed or revoked, in every rollout mode

#### Scenario: Caller manages its own sessions without a permission

- **WHEN** a caller lists or revokes its OWN sessions through these routes
- **THEN** the request is permitted by the owner-id match with no `user.manage`
  grant required, unchanged from current behavior

#### Scenario: Super-admin manages another user's sessions

- **WHEN** a caller with `system.admin` (so `user.manage` is permitted) lists or
  revokes another user's sessions
- **THEN** the cross-user request is permitted and audited, in every rollout mode

### Requirement: Admin user-management pages require user.manage, not the legacy admin claim

The admin user-management Razor pages SHALL gate on an authorizer `user.manage`
decision through the page guard, not on the legacy `freeboard:role=admin` claim. This
covers `Pages/Admin/Users` (its OnGet and its Create, Disable, Enable, and
ResetPassword handlers) and `Pages/Admin/UserCredential` (OnGet). These are pure admin
pages with no
self-service branch, and they read and mutate by calling stores and flows directly
(not through the HTTP API), so no `RequirePermission` endpoint filter and no
route-metadata test reaches them; each handler SHALL call the page guard before
reading or mutating any data. This replaces the previous admin page guard, which read
the legacy claim. A caller holding only the legacy `freeboard:role=admin` claim,
without a `user.manage` (or `system.admin`) grant, SHALL be refused on these pages in
every rollout mode. Because the gate is in-handler, it SHALL be guaranteed by
behavioral web tests, not by the route-metadata test.

#### Scenario: Legacy admin claim cannot reach the admin user-management pages

- **WHEN** a caller holding only the legacy `freeboard:role=admin` claim, with no
  `user.manage` or `system.admin` grant, requests the `Pages/Admin/Users` page (its
  OnGet or any of its Create/Disable/Enable/ResetPassword handlers) or the
  `Pages/Admin/UserCredential` OnGet
- **THEN** the handler returns a bare 403 before reading or mutating any data, in
  every rollout mode

#### Scenario: Super-admin reaches the admin user-management pages

- **WHEN** a caller with `system.admin` (so `user.manage` is permitted) requests these
  pages
- **THEN** the page guard admits the handler

### Requirement: An upsert PUT authorizes both the stored and the requested organisation

A PUT on an existing scope or requirement-scope SHALL authorize the write
permission on BOTH the stored row's current owning organisation AND the requested
new organisation, because the upsert's `ON DUPLICATE KEY UPDATE` can change the
row's `organisation_id` (or an organisation's `parent_id`); authorizing only the
requested organisation would let a caller with write on one organisation overwrite
or MOVE a row currently owned by another. For an organisation PUT: creating a root
organisation (no parent) SHALL require `system.admin`; creating a child
organisation SHALL authorize `org.write` on the parent; reparenting an existing
organisation SHALL authorize `org.write` on BOTH the current parent and the new
parent.

#### Scenario: Cross-org move is denied

- **WHEN** a caller with write on organisation A issues a PUT that would move an
  existing row from organisation B to A, and the caller lacks write on B
- **THEN** the write is denied and the row's owning organisation is unchanged

#### Scenario: Reparent authorizes both parents

- **WHEN** a caller reparents an organisation from parent P1 to parent P2
- **THEN** the write is permitted only if the caller holds `org.write` on both P1
  and P2 (or is a super-admin)

### Requirement: Denied decisions return RFC 7807 problems and are audited

A denied action against a resource the principal can see SHALL return HTTP 403 with
an RFC 7807 problem body, consistent with the existing problem responses. For an
org-scoped resource the principal cannot see at all, the endpoint SHALL return HTTP
404 rather than 403, to avoid disclosing the resource's existence. Every
authorization decision SHALL be logged with a stable event id and structured fields
(actor id, action, resource type, resource id, organisation id, effect, reason),
denies at warning level. The `ILogger` log SHALL always be written and SHALL NOT
depend on the authz store. In addition, the system SHALL persist to the
`authz_audit_events` table exactly this security-relevant set: every denied decision;
every zero-grant `Compat` READ served through the legacy read fallback (the exposure
the Enforce flip closes, so the trail records who used the bridge); and all
authorization, user, and admin mutations (role-assignment writes and user-admin
create/disable/enable/reset). An ordinary permit SHALL be `ILogger`-only, not
persisted, and compliance data writes SHALL be logged only, not persisted. The
persistent write SHALL be
best-effort: on failure it SHALL be skipped and logged and SHALL NOT turn a
permitted request into an error, including when the failed dependency is the authz
store itself. In `Observe` mode the audit SHALL record the would-be decision. The
problem body SHALL NOT contain the internal decision reason.

#### Scenario: Denied action returns a 403 problem

- **WHEN** a visible-resource action is denied
- **THEN** the response is 403 with an RFC 7807 body that omits the internal reason

#### Scenario: Invisible org-scoped resource returns 404

- **WHEN** a principal requests an org-scoped resource it has no access to see
- **THEN** the response is 404, not 403

#### Scenario: Denied decisions and mutations are persisted

- **WHEN** a decision is denied, a zero-grant `Compat` READ uses the legacy read
  fallback, or an authorization, user, or admin mutation occurs
- **THEN** an `authz_audit_events` row is written and the decision is logged, denies
  at warning level

#### Scenario: Ordinary permit is not persisted

- **WHEN** a normally-authorized request is permitted
- **THEN** the permit is logged via `ILogger` only and no `authz_audit_events` row is
  written

#### Scenario: Persistent audit failure is skipped and logged, not fatal

- **WHEN** the persistent `authz_audit_events` write fails (including because the
  authz store itself is the unreachable dependency)
- **THEN** the failure is logged and skipped, the `ILogger` decision log is still
  written, and no otherwise-permitted request is turned into an error

### Requirement: Compliance reads narrow to the caller's authorized subtree

The org-scoped compliance reads SHALL be narrowed to the caller's accessible
organisation set through the `IOrgAccess` seam, whose default implementation
resolves that set by the rollout mode (see the rollout-mode requirement) instead
of narrowing unconditionally. Under `Observe` reads SHALL NOT be narrowed: the
accessible set SHALL be all persisted organisations for every caller regardless of
grants. Under `Compat` the accessible set SHALL be the union of organisation
subtrees on which the caller holds a read-granting role (all organisations for a
super-admin), and a caller with no grants SHALL reach the audited full read
fallback. Under `Enforce` the accessible set SHALL be that same subtree union (all
organisations for a super-admin) and a caller with no read-granting role SHALL have
an empty accessible set. The narrowed reads
SHALL be `GET /organisations` (filtered by id), `GET /scopes` and
`GET /requirement-scopes` (filtered by owning organisation), and the Statement of
Applicability JSON endpoint and page (nodes resolved over the full tree then
filtered to the accessible subtree so inherited dispositions survive). The
non-tenant catalog reads `GET /standards`, `GET /controls`, and
`GET /requirements` SHALL remain authenticated-only and unnarrowed.

#### Scenario: Org-scoped list is narrowed

- **WHEN** a caller whose accessible set is a strict subset of organisations reads
  `/organisations`, `/scopes`, or `/requirement-scopes`
- **THEN** the response contains only rows in or owned by the accessible set

#### Scenario: Super-admin sees the whole domain

- **WHEN** a super-admin reads any org-scoped compliance endpoint
- **THEN** the response contains the whole persisted set

#### Scenario: Catalog reads stay global

- **WHEN** an authenticated caller reads `/standards`, `/controls`, or
  `/requirements`
- **THEN** the full catalog is returned regardless of organisation grants

### Requirement: A named rollout mode governs enforcement, default-deny in all modes

The system SHALL read a rollout mode `Authz:Mode` with values `Observe`, `Compat`,
and `Enforce`. The engine SHALL be default-deny in all three modes. In `Observe`
the authorizer SHALL NOT narrow or block a mode-relaxed org-scoped compliance read
(an accessible-set read); it SHALL log and audit the decision the enforcing path
would make, and those reads SHALL NOT be narrowed (mutating routes still
force-enforce, below). This read relaxation SHALL apply ONLY to the org-scoped
compliance reads and SHALL EXCLUDE the privileged cross-user reads
(`GET /auth/sessions/{id}` and `GET /users/{id}/sessions`), which force-enforce in
every mode (see the cross-user session routes requirement). In `Compat` the
authorizer SHALL
block a denied action, but a caller with no assignments SHALL reach legacy READ
access (full read only) through an explicit, audited fallback, while a caller with
grants SHALL be narrowed to its authorized subtree. There SHALL be NO write
fallback in any mode: a write SHALL always require the proper permission
(`system.admin` or the relevant org write permission), and the legacy
`global_role='admin'` claim SHALL NOT grant authorization. A `super-admin` system
assignment SHALL be the sole source of system power, so revoking it removes write
access even though the caller may still carry the legacy admin claim. In `Enforce`
there SHALL be no legacy read fallback either. The `Authz:Mode` relaxation SHALL govern
READS only. EVERY mutating route - compliance writes, user-admin mutations, and
role-assignment management - SHALL force-enforce through the `RequirePermission`
filter's `alwaysEnforce` option (backed by a mode-independent authorizer decision) that
bypasses the mode relaxation and blocks a `Deny` in every mode, including `Observe` and
`Compat`; no mutating route SHALL fall through the mode relaxation. A super-admin SHALL
be permitted everywhere in every mode.

#### Scenario: Observe logs without blocking

- **WHEN** the mode is `Observe` and a mode-relaxed org-scoped compliance read (an
  accessible-set read) would be denied under enforcement
- **THEN** the request is not narrowed or blocked and the would-be decision is logged
  and audited

#### Scenario: Compat lets a zero-grant caller read through an audited fallback

- **WHEN** the mode is `Compat` and a caller with no assignments makes a READ
  request that enforcement would deny
- **THEN** the read is allowed through the legacy read fallback and the fallback use
  writes an `authz_audit_events` row recording who used the bridge

#### Scenario: Compat denies a zero-grant caller a compliance write

- **WHEN** the mode is `Compat` and a non-admin caller with no assignments attempts
  a compliance write
- **THEN** the write is denied - there is no admin-claim write fallback - even though
  the same caller keeps the legacy read fallback

#### Scenario: Enforce blocks a zero-grant caller

- **WHEN** the mode is `Enforce` and a caller with no assignments makes a request
  it is not permitted
- **THEN** the request is denied

#### Scenario: Management endpoints enforce in every mode

- **WHEN** a caller without the required permission calls a role-assignment
  management endpoint in any mode
- **THEN** the request is denied

#### Scenario: Denied management mutation is blocked in Observe

- **WHEN** the mode is `Observe` and a caller without `authz.assignment.write` (and
  not a super-admin) calls a role-assignment mutation
- **THEN** the force-enforce path blocks it with a problem response, even though
  ordinary reads do not block in `Observe`, and no grant changes

#### Scenario: Denied compliance write is blocked in Observe

- **WHEN** the mode is `Observe` and a caller without the required org write
  permission (and not a super-admin) attempts a compliance write
- **THEN** the force-enforce path blocks it with a problem response, even though reads
  are not narrowed in `Observe`, and the store is unchanged

#### Scenario: Denied user-admin mutation is blocked in Observe

- **WHEN** the mode is `Observe` and a caller without `user.manage` (and not a
  super-admin) attempts a user-admin mutation
- **THEN** the force-enforce path blocks it with a problem response and no user changes

### Requirement: Bootstrapping and last-super-admin prevention

The first user created through the existing setup flow SHALL be granted the
`super-admin` system role in the same transaction that creates the user, the
password credential, and the bootstrap marker, so the system is always
administrable without a separate seed step. A user created with `global_role='admin'`
through the user-admin API SHALL likewise receive its `super-admin` system
assignment in the same transaction as the user row AND its authentication credential
(and any force-reset state); if the credential write fails, the whole create SHALL
roll back, leaving no orphan `super-admin`, so an admin-created super-admin is never
counted while unable to authenticate. An admin created via invite SHALL defer its
authentication credential until the invite is accepted, and SHALL NOT be counted as a
usable super-admin until then; the usable-admin count already leaves an unaccepted
invite uncounted because it has no credential, consistent with D12. This also prevents
lock-out once the legacy admin claim no longer grants authorization. Creating an organisation through the
write API SHALL grant the creator `org-owner` on that organisation unless the creator
is a super-admin. The system SHALL prevent lockout, and each lockout guard SHALL be
enforced atomically inside the single locking write-store transaction that performs
the mutation (not as a separate endpoint check-then-write), so two concurrent revokes
or disables cannot both pass. An "active super-admin" (a USABLE administrator) is a
user that is enabled, holds `super-admin`, AND has an authentication credential; a
disabled super-admin, or one with no credential, does not count. The "last org-owner"
is the last DIRECT `org-owner` assignment on the organisation, not one effective
through an ancestor grant. Revoking the last active `super-admin` SHALL be rejected;
disabling the user who is the last active `super-admin` SHALL be rejected, including
through the `POST /api/v1/freeboard/users/{id}/disable` endpoint; revoking the last
direct `org-owner` of an organisation, or one's own last direct `org-owner` grant,
SHALL be rejected.

#### Scenario: First admin is granted super-admin atomically

- **WHEN** the first admin user is created through the setup flow
- **THEN** the user and its `super-admin` system assignment are committed in one
  transaction

#### Scenario: API-created admin is granted super-admin atomically

- **WHEN** an admin (`global_role='admin'`) user is created through the user-admin
  API
- **THEN** the user row, its authentication credential, and its `super-admin` system
  assignment are committed in one transaction, so the admin can authenticate and
  administer under Enforce

#### Scenario: Failed credential write during admin create leaves no super-admin

- **WHEN** an admin user is created and the credential write fails
- **THEN** the whole create rolls back and no `super-admin` assignment remains, so no
  orphan super-admin is counted

#### Scenario: Invited admin is not counted until the invite is accepted

- **WHEN** an admin is created via invite and has not yet accepted, so it holds no
  authentication credential
- **THEN** it holds a `super-admin` assignment but is not counted as a usable
  super-admin, so it cannot be treated as the last usable admin

#### Scenario: Concurrent last-super-admin revokes or disables cannot both pass

- **WHEN** two revokes of the last active `super-admin`, or two disables of the last
  two usable super-admins, or a revoke racing a disable of the last two, run
  concurrently
- **THEN** the atomic guard lets at most one proceed and rejects the rest, so a usable
  super-admin always remains

#### Scenario: Creator becomes owner of a new organisation

- **WHEN** a non-super-admin creates an organisation through the write API
- **THEN** the creator is granted `org-owner` on it

#### Scenario: Last super-admin cannot be disabled through the API

- **WHEN** a disable request targets the user who is the last active super-admin
- **THEN** the endpoint rejects it and the account stays enabled

#### Scenario: Last owner cannot be revoked

- **WHEN** a revoke would remove the last `org-owner` of an organisation
- **THEN** the revoke is rejected and the grant remains

### Requirement: The rollout preserves existing access when shipped in Compat

Shipping in `Compat` with the migration backfill SHALL NOT deny any request that is
permitted today. The global-admin-derived `super-admin` SHALL permit every action.
Existing members SHALL be backfilled to `compliance-reader` on the current root
organisations so they retain whole-tree read. Moving the compliance write endpoints
from the admin-claim policy to permission checks SHALL remain additive: super-admins
still pass and org owners and compliance managers gain scoped write. User
administration SHALL remain super-admin-only, so its behavior is unchanged.

#### Scenario: Existing admin retains write access

- **WHEN** a global-admin-derived super-admin calls a compliance write endpoint
  after the change in Compat
- **THEN** the write is permitted as before

#### Scenario: Backfilled member retains whole-tree read

- **WHEN** an existing member backfilled to `compliance-reader` on the root
  organisations reads the compliance domain in Compat
- **THEN** the read returns the whole tree

#### Scenario: Org owner gains scoped write

- **WHEN** a non-super-admin `org-owner` of O writes a compliance resource in O's
  subtree
- **THEN** the write is permitted, a capability that did not exist before
