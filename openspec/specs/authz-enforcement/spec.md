# authz-enforcement Specification

## Purpose
TBD - created by archiving change add-authz-foundation. Update Purpose after archive.
## Requirements
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

A PUT on an existing scope (a standard-level or a requirement-level disposition) SHALL
authorize the write permission on BOTH the stored row's current owning organisation AND the
requested new organisation, because the upsert's `ON DUPLICATE KEY UPDATE` can change the
row's owning subject (or an organisation asset's `parent`); authorizing only the requested
organisation would let a caller with write on one organisation overwrite or MOVE a row
currently owned by another. The owning organisation of a scope row is read from the unified
`scopes` table's `subject_id` column (the former `scopes.organisation_id` and
`requirement_scopes.organisation_id`, now one column holding the organisation asset id),
because the two per-target tables merged into one `scopes` table.

Because both app-managed write routes now target that one `scopes` table, each route SHALL
be confined to its own target kind so an id cannot cross the target boundary, WITHOUT breaking
creation of a new scope. A PUT SHALL branch on the addressed `id`'s current existence: an
absent `id` creates a row of the route's own target kind, an `id` whose existing row is the
route's own target kind is updated, and an `id` whose existing row is a DIFFERENT target kind
is treated as not-found and SHALL NOT be converted. A DELETE SHALL affect only rows whose own
target column is set (the standard-level route `PUT`/`DELETE /scopes/{id}`,
`compliance.scope.write`, only `standard_id` rows; the requirement-level route
`PUT`/`DELETE /requirement-scopes/{id}`, `compliance.requirement-scope.write`, only
`requirement_id` rows). A caller holding one write permission therefore cannot read,
overwrite, or delete the other route's target-kind row by id, while an ordinary new-id PUT
still creates. Control-target scope rows are gitops-write-only and have NO app write path, so
neither route may create, modify, or delete them.

The stored owning organisation the PUT and DELETE authorize is read from that one `scopes`
table by `id`, so this same target-column confinement SHALL bind the stored-owner authorization
lookup itself: each route SHALL read the stored owner only from a row whose own target column
matches the route, treating a same-`id` row of a different target kind as absent. A wrong-kind
row is therefore never authorized against - the caller is neither granted the write nor 403'd
against that other-kind row's owning organisation; the write resolves to not-found (404), and
the other-kind row's owner is not disclosed through an authorization decision.

For an organisation PUT: creating a root organisation (no parent) SHALL require
`system.admin`; creating a child organisation SHALL authorize `org.write` on the parent;
reparenting an existing organisation SHALL authorize `org.write` on BOTH the current parent
and the new parent.

#### Scenario: Cross-org move is denied

- **WHEN** a caller with write on organisation A issues a PUT that would move an
  existing row from organisation B to A, and the caller lacks write on B
- **THEN** the write is denied and the row's owning organisation is unchanged

#### Scenario: Reparent authorizes both parents

- **WHEN** a caller reparents an organisation from parent P1 to parent P2
- **THEN** the write is permitted only if the caller holds `org.write` on both P1
  and P2 (or is a super-admin)

#### Scenario: One write permission cannot reach the other route's target-kind row

- **WHEN** a caller holding only `compliance.scope.write` addresses the id of a
  requirement-target scope row through `PUT`/`DELETE /scopes/{id}`, or a caller holding only
  `compliance.requirement-scope.write` addresses a standard-target row through
  `PUT`/`DELETE /requirement-scopes/{id}`
- **THEN** the row is treated as not-found and is neither deleted nor converted to the other
  target kind, because each route is confined by its own target column and a control-target
  row is reachable by neither route

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

The org-scoped compliance reads SHALL be narrowed through a single accessibility seam that
resolves the caller's ACCESSIBLE ASSET set over the unified asset tree. The seam SHALL derive
that set in two steps, and its default implementation SHALL resolve the first step by the
rollout mode (see the rollout-mode requirement) instead of narrowing unconditionally.

Step one is the ORGANISATION read-subtree union, unchanged from the organisation-only model:
under `Observe` reads SHALL NOT be narrowed and the union SHALL be all persisted organisations
for every caller regardless of grants; under `Compat` the union SHALL be the union of
organisation subtrees on which the caller holds a read-granting role (all organisations for a
super-admin), and a caller with no grants SHALL reach the audited full read fallback; under
`Enforce` the union SHALL be that same subtree union (all organisations for a super-admin) and
a caller with no read-granting role SHALL have an empty union. Authorization GRANTS remain
rooted on organisations; no grant is assigned on a non-organisation asset.

Step two SHALL close that union over the asset tree, deciding each asset by the edge it
carries rather than by its type, because `parent` and `owner` are mutually exclusive:

- an asset that carries a `parent` SHALL be accessible when its cycle-guarded inclusive
  `parent` chain intersects the organisation union;
- an asset that carries an `owner` (a `Vendor`) SHALL be accessible when its `owner` is in the
  organisation union;
- an asset that carries neither SHALL be accessible only when its own id is in the
  organisation union.

A discovered asset in the `Retired` state SHALL NOT be in the accessible set: a retired asset
is not a live authorization anchor.

A missing or dangling edge SHALL resolve to no hit rather than to a wildcard, so read-access
is fail-closed. The exclusion is decided by the whole chain, not by the edge alone: the
`parent` chain is INCLUSIVE and its first entry is the asset itself, so an ORGANISATION whose
own id is in the union SHALL remain accessible even when its `parent` names an id no asset
defines or sits in a `parent` cycle. What a dangling or missing edge excludes is an asset that
has no other way in - a `Machine` whose chain reaches no organisation in the union, and a
`Vendor`, which has a single `owner` edge and no inclusive chain to fall back on, so an
ownerless or dangling-owner vendor SHALL NOT be accessible to any caller. Global vendor
readability - every authenticated user seeing every vendor regardless of grants - SHALL NOT
apply.

The rollout mode SHALL govern STEP ONE only. The `Observe` relaxation and the `Compat`
zero-grant fallback widen the ORGANISATION union; they SHALL NOT disable, weaken, or bypass any
step-two edge rule. Under every mode, an asset whose inclusive chain reaches no organisation in
the union, and a discovered asset in the `Retired` state, SHALL be outside the accessible set
even when the union is every organisation. A mode changes which organisations anchor a read,
never whether an edge has to resolve.

The narrowed reads SHALL be `GET /organisations` (filtered by id against the accessible asset
set), `GET /vendors` (filtered by id against the same set, which admits a vendor exactly when
its `owner` is in the organisation union), the unified `GET /scopes` (filtered by whether the
scope's `subject` id is in the accessible asset set, which is one membership test covering the
organisation, vendor-`owner`, and machine-`parent`-ancestry cases that were previously three
per-surface rules), and the Statement of Applicability JSON endpoint and page (nodes resolved
over the full tree then filtered to the accessible asset set so inherited dispositions
survive). The non-tenant catalog reads `GET /standards`, `GET /controls`, and
`GET /requirements` SHALL remain authenticated-only and unnarrowed.

`GET /collectors` and `GET /integration-connections` SHALL likewise keep their full ROW
sets for every authenticated caller - they carry no organisation dimension - but SHALL
NOT emit a `vendor` id outside the caller's accessible asset set: the `vendor` field on
each row SHALL be emitted only when that vendor id is in the set, and as `null`
otherwise. The same field rule SHALL apply to the collector register page, the
integration-connections page, and the Statement of Applicability drill-down, which SHALL show
a check's vendor title only when that vendor is in the accessible asset set and SHALL NOT fall
back to the raw vendor id. This is what makes global vendor readability actually
dropped: narrowing `/vendors` alone leaves a hidden vendor's id readable from a collector
or connection row. The rule bounds the vendor ASSET id only; the other fields of those
rows describe the collector or the connection rather than the vendor asset, and are not
narrowed by it.

#### Scenario: Org-scoped list is narrowed

- **WHEN** a caller whose accessible set is a strict subset of the asset tree reads
  `/organisations`, `/vendors`, or the unified `/scopes`
- **THEN** the response contains only rows whose id, or whose subject id, is in the
  accessible asset set

#### Scenario: A machine is accessible through its parent chain

- **WHEN** a caller holds a read grant on a company and a `Machine`'s `parent` chain reaches
  that company through a department
- **THEN** the machine is in the caller's accessible asset set, and a machine whose chain
  reaches no organisation in the union is not

#### Scenario: A vendor is accessible only through its owner

- **WHEN** a caller's organisation union contains a vendor's `owner`
- **THEN** the vendor is in the caller's accessible asset set; and a vendor whose `owner` is
  missing, dangling, or outside the union is not, for any caller including one with grants
  elsewhere in the tree

#### Scenario: A dangling parent does not hide an organisation from its own grant

- **WHEN** a caller's organisation union contains a `Department` whose `parent` names an id
  no asset defines, and another whose `parent` chain sits in a cycle
- **THEN** both are in the caller's accessible asset set, because the inclusive chain starts
  at the asset itself, while a `Machine` whose chain reaches no organisation in the union is
  not

#### Scenario: A retired discovered asset is never accessible

- **WHEN** a discovered `Machine` in the `Retired` state has a `parent` chain reaching the
  caller's organisation union
- **THEN** it is not in the accessible asset set, so neither it nor a scope naming it as
  subject is returned

#### Scenario: Observe widens the union but keeps the edge rules fail-closed

- **WHEN** the rollout mode is `Observe` and a caller with no grants reads an org-scoped
  compliance endpoint while a vendor has no `owner` and a discovered `Machine` is `Retired`
- **THEN** the caller's organisation union is every persisted organisation, so every asset
  whose `parent` chain or `owner` reaches an organisation in that union is accessible, but the
  ownerless vendor, the retired discovered machine, and any asset whose chain reaches no
  organisation in the union are still outside the accessible set

#### Scenario: Super-admin sees the whole readable domain

- **WHEN** a super-admin reads any org-scoped compliance endpoint
- **THEN** the response contains the whole READABLE persisted set - every asset anchored in
  the super-admin's whole-tree organisation union, including live `Machine` and owner-anchored
  `Vendor` assets, and every scope whose subject is one of them - while a scope whose subject
  is unresolved (no asset row, or a retired discovered asset) or unsupported (a deferred group
  subject) is still OMITTED even for the super-admin, consistent with the
  `compliance-web-read` fail-closed rule; such a scope surfaces only through the generic
  dangling-subject warning, never as a `/scopes` row

#### Scenario: Catalog reads stay global

- **WHEN** an authenticated caller reads `/standards`, `/controls`, or
  `/requirements`
- **THEN** the full catalog is returned regardless of organisation grants

#### Scenario: A hidden vendor's id does not leak through the global reference reads

- **WHEN** an authenticated caller with no grant reaching a vendor's `owner` reads
  `/collectors` or `/integration-connections` and a row names that vendor
- **THEN** every row is still returned and the row's `vendor` reads `null`, so the caller
  cannot learn the id of a vendor outside its owner's subtree from any read surface

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

### Requirement: An organisation gate anchors only on organisation ancestry

An authorization check whose resource carries an organisation id SHALL anchor that id on an
inclusive ancestry chain that STOPS at the first entry naming an asset that is not a `Company`
or `Department`. That entry SHALL be kept and the chain SHALL go no further, so no grant held
beyond it can match. An entry naming no asset at all SHALL be kept and SHALL NOT stop the
chain, because the chain already ends there for want of a `parent`; an id that names no asset
therefore anchors only itself, and creating an organisation at the root still requires
`system.admin`.

The chain SHALL stop at a non-organisation entry wherever it appears, not only at the supplied
id. Ancestry is INCLUSIVE and the asset tree is one tree, so a `Machine` hanging under a
department resolves through that department to its company, and an ORGANISATION whose `parent`
names a machine resolves on through that machine into a second organisation subtree. Both are
grant matches the organisation-only ancestry never produced, and both are closed by stopping
at the same point that walk stopped.

This rule holds wherever an organisation-scoped resource is BUILT, whatever the id's provenance
and whatever it names: the organisation upsert on both its create and its update arm, the
organisation delete, the scope and requirement-scope writes and deletes including their
stored-owner lookups, the cross-organisation-move and parent-side reparent checks, the
role-assignment API, and the role-assignment page. Confining an id to an organisation ASSET
before it is used SHALL NOT exempt it, because an organisation's own `parent` may name a
non-organisation asset and carry the chain on through it.

A request refused by this rule SHALL be refused as an audited authorization decision, and
SHALL NOT be allowed to reach the persistence layer and be answered there as a `404`, a
validation `400`, or a no-op success. The write store's own type predicates remain as defence
in depth for a caller that arrives by some other path; they are not the enforcement point. The
refused request SHALL carry the status its route gives today: `403` for a resource the caller
can see, and, where a route resolves visibility for itself before this rule applies, the `404`
that existing visibility rule produces.

#### Scenario: A machine id on an organisation route is refused

- **WHEN** a caller who is not a super-admin, but who holds the route's permission on a
  `Machine`'s parent organisation, supplies that machine's id as the organisation of a
  compliance write or an organisation delete or reparent
- **THEN** the request is denied with `403` and audited as a denial, and the write store is
  never reached

#### Scenario: A role-assignment route keeps existence non-disclosure

- **WHEN** that same caller supplies the machine's id as the `orgId` of a role-assignment
  route whose selector already gates on whether the caller can read that organisation
- **THEN** the read check denies under `Enforce`, so the route answers `404` and discloses
  nothing about the id, and the assignment is neither listed nor mutated

#### Scenario: An organisation parented onto a machine anchors no grant beyond it

- **WHEN** an organisation's `parent` names a `Machine` that itself hangs under another
  organisation subtree, and a caller holds the route's permission only in that further
  subtree
- **THEN** the chain stops at the machine, so no grant in the further subtree matches and the
  request is denied

#### Scenario: An unknown id still resolves as a create

- **WHEN** a caller upserts an organisation at an id that names no asset
- **THEN** the id anchors only itself, so the call requires `system.admin` exactly as it does
  for any other root creation

#### Scenario: A super-admin is unaffected

- **WHEN** a super-admin supplies a non-organisation asset id on an organisation-scoped route
- **THEN** the super-admin permission permits the call, and the route's own type handling
  decides the outcome from there

### Requirement: The accessible asset set is memoized per asset list, not per principal alone

The accessibility seam SHALL memoize a caller's accessible asset set per principal AND per asset
list within a request, and SHALL resolve it at most once for each such pair. It SHALL NOT serve a
set resolved from one asset list to a caller that supplied a different one.

The seam SHALL keep its contract that the caller passes the UNFILTERED asset list. A caller still
cannot narrow before it calls, and still cannot assume it will be served a set derived from the
list it just supplied unless that list is the one the set was resolved from.

Keying on the principal alone is what forces every surface in a request onto one store read: the
first asset list to reach the seam decides the set every later surface is served, so a surface
reading its own rows narrows them with another surface's owner edges. Keying on the pair is what
lets a decision take exactly the read its own inputs need.

#### Scenario: Two asset lists resolve two sets

- **WHEN** one request resolves the accessible set for one principal from two different asset
  lists
- **THEN** each resolution is derived from the list it was given, and neither is served the
  other's answer

#### Scenario: One asset list resolves one set however many callers ask

- **WHEN** several surfaces in one request each ask the seam for the accessible set and each
  passes the same asset list
- **THEN** the set is resolved once and the later asks are served that answer, so a page render
  does not repeat the ancestry walk

### Requirement: The shared asset read carries no payload table

The request's shared asset read SHALL NOT cause any table but the unified `assets` table to be
read. That read is the one every organisation gate, every compliance write selector, and every
role-assignment guard draws on. A decision that gates on an organisation narrows the asset tree
only, so no payload table SHALL become an input it depends on.

ONE class of gate is excepted, and only that one: a gate whose organisation is DERIVED FROM A
STORED ROW rather than named by the route or the request body. Such a gate cannot know which
organisation to authorize against until it has read the row, and the row and the asset that
resolves it SHALL come from one snapshot, so that gate's asset read names the row's table too.
Today that is the scope and requirement-scope writes, whose organisation comes from the stored
scope row, so their snapshot names the assets and the scopes. Every OTHER gate - route-anchored,
body-anchored, and both role-assignment guards - SHALL keep reading the `assets` table alone. A
request whose first asset read is one of those gates therefore reads the `assets` table alone,
whatever the later surfaces of that request go on to read.

The exception is bounded by what the excepted gate already needs. It SHALL extend only to the
table holding the row the gate reads to find its organisation, and that row's table SHALL be one
the gated write already requires. A gate SHALL NOT be widened with a table the write itself does
not need.

A surface SHALL NOT widen the shared asset read in order to obtain a pairing it needs. A surface
that reads a payload list together with the assets keeps that pairing in its OWN read, and the
accessible set is resolved per asset list, so that surface is narrowed by its own snapshot's
owner edges whatever order the surfaces of a request run in. Which surfaces owe that pairing is
stated by the capability that owns each of them.

This bounds the blast radius of a missing table. A payload table absent from the schema SHALL
degrade the surfaces that read it and SHALL NOT make an organisation gate or a compliance write
fail closed. The excepted gates give up no part of that: the table their snapshot names is the
one holding the row the write targets, so a schema without it fails that write either way, and
the failure moves from the store call to the gate rather than appearing where it did not before.

#### Scenario: An organisation gate reads no payload table

- **WHEN** a compliance write gated on an organisation named by the route or the request body,
  or a role-assignment page, is gated and the request has taken no other compliance read
- **THEN** the gate resolves its asset tree from a read of the `assets` table alone, and no
  vendor assurance row is read

#### Scenario: A stored-row gate names the row's table and nothing more

- **WHEN** a scope write is gated by deriving its organisation from the stored scope row, and the
  request has taken no other compliance read
- **THEN** the gate takes ONE snapshot of the assets and the scopes, so the row and the asset
  that resolves its subject cannot straddle a commit, and it names no other payload table

#### Scenario: The shared asset read is served from a snapshot already taken

- **WHEN** a request renders a surface that takes an asset-and-payload snapshot, and an
  organisation gate in the same request then asks for the shared asset list
- **THEN** the gate is served that snapshot's asset rows, no second read is taken, and it is
  narrowed by the accessible set resolved from those rows

#### Scenario: A failed payload read leaves the shared asset read intact

- **WHEN** a surface's asset-and-payload snapshot faults and an organisation gate in the same
  request then asks for the shared asset list
- **THEN** the gate reads the `assets` table alone and answers, because nothing was memoized for
  it to be served from

#### Scenario: A missing payload table does not close the write path

- **WHEN** the app runs against a schema whose vendor assurance table is absent
- **THEN** gated compliance writes and the role-assignment page keep working, and only the
  surfaces that read the assurances degrade

### Requirement: One authorization decision draws its inputs from one snapshot

An authorization decision on the compliance domain SHALL draw both its inputs from ONE store
snapshot: the ROWS it answers with, and the ASSET LIST whose `parent` and `owner` edges decide
which of those rows the caller may see.

This applies to every decision, read or write, that narrows by the accessible asset set or
that resolves an authorization anchor from a stored row: the narrowed read endpoints, the
server-rendered register pages, the Statement of Applicability page and endpoint, the shell's
organisation selector and nav badge, and the write-path selector that derives a scope row's
owning organisation before gating on it. A decision SHALL NOT pair rows read on one connection
with an asset list read on another, because an importer commit between the two reads produces a
combination that never existed: pre-commit owner edges deciding what post-commit rows may
disclose, or the reverse.

Where a gate resolves an organisation ancestry chain for a decision whose organisation was
itself derived from a stored row, that chain SHALL be resolved from the SAME snapshot the row
came from, not from a separately taken asset read.

A decision MAY draw a read that takes no part in deciding what the caller may see from outside
its snapshot. The criterion is participation in the visibility decision, not participation in
the response. A reference label that already degrades to an identifier when unresolvable, and an
existence check that decides not-found rather than visibility, are outside; any list the
decision narrows, or narrows BY, is inside.

Two decisions rendered in one response MAY come from two snapshots. Each SHALL be internally
consistent, and neither SHALL narrow with the other's asset list. This rests on the accessible
set being memoized per asset list, which this capability already requires: a decision is never
served a set resolved from another decision's rows. A count derived from one snapshot beside a
table derived from another is a stale count that self-corrects on the next request, which is a
different thing from a response that pairs one state's rows with another state's owner edges.

The guarantee is that no decision MIXES two states of the domain. It is NOT that a decision
always reflects the latest committed state. A snapshot opened before an importer commits SHALL
be permitted to answer wholly from the pre-commit state after that commit lands, and such an
answer SHALL be considered correct: it is a state the database really held, and the next
request reads the new one. What SHALL NOT happen is a single decision built from both states -
pre-commit owner edges deciding what post-commit rows disclose, or the reverse.

#### Scenario: Rows and the narrowing asset list come from one snapshot

- **WHEN** a narrowed compliance surface renders while a GitOps sync commits a reparenting that
  moves an asset across the caller's accessible boundary
- **THEN** the rows and the asset list that narrows them are both from ONE side of that commit,
  so the response contains no row, and no excluded-scope justification, that the owner edges it
  was read with do not admit

#### Scenario: A wholly pre-commit answer is not a violation

- **WHEN** a snapshot opens before an importer commit and the response returns after it
- **THEN** the response MAY show the pre-commit rows and the pre-commit owner edges together,
  because that pairing is a state the database held, and the requirement forbids a mixture
  rather than staleness

#### Scenario: A stored-row gate anchors on the snapshot it read the row from

- **WHEN** a write is gated by deriving the owning organisation from a stored scope row and then
  resolving that organisation's ancestry chain
- **THEN** the row and the ancestry chain come from one snapshot, so the write is not authorized
  against an ancestry the row's own snapshot never had

#### Scenario: Two decisions in one response are each internally consistent

- **WHEN** one page render takes two snapshots because the second decision needs a list the
  first did not read
- **THEN** each decision narrows with the asset list of its own snapshot, and neither is
  narrowed by the other's

#### Scenario: A reference label may sit outside the snapshot

- **WHEN** a surface renders a standard's title beside a narrowed row and reads the standards
  separately
- **THEN** that is permitted, because an unresolvable title renders as the standard id and the
  read decides no visibility

