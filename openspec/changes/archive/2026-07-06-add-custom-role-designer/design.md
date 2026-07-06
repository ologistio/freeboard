## Context

Roles are already data. `010_authorization.sql` created `authz_roles(role_key,
title, description, scope, is_system, created_at, updated_at)`,
`authz_permissions(permission_key, ...)`, and `authz_role_permissions(role_key,
permission_key)`. `authz_roles.is_system` distinguishes seeded (1) from custom
(0); `authz_roles.scope` (`system` or `organisation`) is a persisted invariant
that the fact loader (`MySqlAuthzStore`) joins on, so a mis-scoped row contributes
nothing. The write store (`MySqlAuthzAdministrationStore`) already validates scope
before assignment writes and returns `AuthzWriteResult` (`Ok` / `NotFound` /
`Conflict` / `Invalid`) mapped to status codes by the endpoint layer.

Grounded facts (read from the repo, not assumed):

- `authz_roles.role_key` is `VARCHAR(64)` `utf8mb4_bin`. The four seeded keys
  (`super-admin`, `org-owner`, `compliance-manager`, `compliance-reader`) contain
  no colon, so a colon-bearing prefix cannot collide with a current seed.
- The assignment tables FK to `authz_roles.role_key` `ON DELETE RESTRICT`;
  `authz_role_permissions` FKs to the role `ON DELETE CASCADE`. So deleting a role
  removes its permission rows, and a live assignment blocks the delete at the DB.
- `MySqlAuthzAdministrationStore.AppendAuditEventAsync` opens its OWN connection,
  separate from any mutation transaction, and `AuthzMutationAudit.AppendAsync`
  swallows failures behind `ILogger`. Today's audit is out-of-band and
  best-effort.
- The eight seeded permission keys and their `resource_type` are known; the five
  org-tree read/write keys are `org.read`, `org.write`, `compliance.read`,
  `compliance.scope.write`, `compliance.requirement-scope.write`.

Enforcement reads the same tables regardless of edition. The entitlement seam
(`IEnterpriseEntitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies)`) is
registered in `Program.cs` (`ConfigurationEnterpriseEntitlements`, default off)
but is not consumed anywhere yet. This change is its first consumer.

Constraints: the one-way EE rule (`Freeboard.Enterprise` -> `Freeboard.Core`
only; the web app is the sole combiner of Core + EE + Persistence);
`MySqlAuthzAdministrationStore` lives in `Freeboard.Persistence`, which references
`Freeboard.Core` but never `Freeboard.Enterprise`; SSR over client reactivity for
web UI; the route-metadata architecture test (`RouteAuthzMetadataTests`) requires
every mutating gated route to carry `AuthzPermissionMetadata { AlwaysEnforce =
true }`.

## Provenance and divergence resolution

This design merges two independent plans for issue #24: Plan A (the prior
OpenSpec artifacts in this change) and Plan B (an independent plan). Both agreed
on the core shape: no migration, security floor in MIT persistence, super-admin +
force-enforce gate, entitlement gate that returns 404 when off, audit on every
mutation, in-use delete protection. They diverged on five points, resolved below
against the real code and recorded as decisions D2, D4, D3, D8, D10.

- D2 store shape: kept A (reuse the two existing seams) over B (a new
  `IAuthzRoleStore`). Lower liability, no new DI or fake.
- D4 namespace: kept A's flat `custom:` prefix, added B's bounded-slug
  discipline, dropped B's `:org:` middle segment as speculative.
- D3 catalog location: adopted B (Core owns the enforced allow-list; EE holds
  presentation metadata only) over A (EE owns the authorable catalog). Keeps the
  security-relevant set in MIT, reachable by the write store.
- D8 audit: adopted B (audit row written inside the mutation transaction) over
  A (best-effort shared helper), because the real audit path is out-of-band and
  best-effort and cannot guarantee "every mutation writes an audit row".
- D10 immutability: adopted B (`role_key` immutable after create).

## Goals / Non-Goals

**Goals:**

- Super-admin CRUD over custom organisation-scoped roles, page plus API.
- Security floor enforced in MIT persistence, independent of the entitlement.
- Entitlement-gated authoring surface; seeded roles and enforcement unaffected
  when the gate is off.
- Every mutation audited atomically; every new mutating route force-enforces.

**Non-Goals:**

- No system-scoped custom roles. `scope` is fixed to `organisation` at create and
  is never editable.
- No new permission keys and no new authz tables or migration. Custom roles
  compose the five authorable org-tree keys only.
- No assignment UI changes. Granting a custom role to a user reuses the existing
  role-assignment surface (`RoleAssignmentEndpoints`, `/admin/role-assignments`),
  which already accepts any organisation-scoped `role_key`.
- No per-organisation ownership of a role definition. Custom roles are global
  definitions authored by a super-admin (hence the `system.admin` gate), even
  though each definition is `organisation`-scoped when assigned.
- No `role_key` rename in place. Renaming is delete-then-recreate while unused
  (D10). No second scope family (for example API-credential scoping) is built now.

## Decisions

### D1. No migration; reuse the existing schema

`010_authorization.sql` already stores everything a custom role needs. Custom
roles are `authz_roles` rows with `is_system = 0`, `scope = 'organisation'`, and
`authz_role_permissions` rows referencing existing `authz_permissions` keys. Adding
a migration would be pure liability. Alternative considered: a `CHECK`-style DB
guard on authorable keys; rejected because MySQL enforcement is app-side already
(scope invariant), the allow-list floor is cleaner in one place in the store, and
a DB constraint cannot express the authorable set as maintainably.

### D2. Role read on `IAuthzStore`, role write on `IAuthzAdministrationStore`

Source: A (kept over B). Reuse the two existing authz seams instead of adding a
third interface. Assignment reads already live on `IAuthzStore` and assignment
writes plus the audit append already live on `IAuthzAdministrationStore`; role
reads and writes follow the same split. Both are already registered by `AddAuthz`
and already have web-test fakes, so no new DI and no new fake type.

- `IAuthzStore` gains `ListCustomRolesAsync()` and `GetRoleAsync(roleKey)`
  (returns the role plus its permission keys, or null).
- `IAuthzAdministrationStore` gains `CreateCustomRoleAsync`,
  `UpdateCustomRoleAsync`, `DeleteCustomRoleAsync`, all returning
  `AuthzWriteResult`.
- New read-model records in `AuthzReadModels.cs`: `CustomRoleRow(RoleKey, Title,
  Description, Scope, IsSystem, CreatedAt, UpdatedAt)` and
  `RoleWithPermissions(CustomRoleRow Role, IReadOnlyList<string> PermissionKeys)`.

Alternative considered (Plan B): a dedicated `IAuthzRoleStore` with
`MySqlAuthzRoleStore`. Rejected: an extra interface, impl, DI line, and fake for
no cohesion win. The administration store is already "the authz mutation surface"
and already owns audit writes, so it is the natural home for role writes,
especially given D8 (the audit row is written by the store, in the same
transaction).

### D3. Core owns the enforced authoring rules; the write store enforces them

Source: B (adopted over A), reconciled with A's negative floor. The
security-relevant set must be reachable by `MySqlAuthzAdministrationStore`, which
lives in `Freeboard.Persistence` and cannot reference `Freeboard.Enterprise`. So
the enforced rules live in `Freeboard.Core` (MIT), in a focused static type
`AuthzCustomRoles`:

- `CustomRoleKeyPrefix = "custom:"` (D4).
- `AuthorablePermissionKeys`: the five org-tree keys, referencing `AuthzActions`
  constants (`OrgRead`, `OrgWrite`, `ComplianceRead`, `ComplianceScopeWrite`,
  `ComplianceRequirementScopeWrite`).
- `IsAuthorableRoleKey(roleKey)`: prefix present, remainder a bounded lowercase
  ASCII hyphenated slug, total length <= 64 (fits the column).

The write store enforces, independent of any entitlement:

- `scope` and `is_system` are not caller-settable. The create signature carries no
  `scope`; the store writes `scope = 'organisation'` (`AuthzRoles.ScopeOrganisation`)
  and `is_system = 0` as constants, so it can never mint a non-organisation or
  system custom role. There is no scope value to reject, so no "non-organisation
  scope" rejection path exists at create (unlike the assignment writes, which read
  the target role's persisted `scope` and reject a mismatch - a check over
  persisted data, not caller input).
- `role_key` must satisfy `AuthzCustomRoles.IsAuthorableRoleKey`; otherwise
  `Invalid`.
- `title` must be non-blank and at most 190 characters. `description` is optional: a
  null or omitted value is coerced to an empty string, and only an over-length value
  (> 512 characters) is rejected `Invalid` (the grounded
  `authz_roles.title VARCHAR(190) NOT NULL` and
  `authz_roles.description VARCHAR(512) NOT NULL` column widths; the coercion keeps
  the NOT NULL column satisfied). A title violation writes nothing. The endpoint
  validates the same before calling the store (fail fast); the store re-validates and
  coerces (defence in depth).
- Every submitted permission key must be in
  `AuthzCustomRoles.AuthorablePermissionKeys`; otherwise `Invalid`, writing
  nothing. This single positive allow-list check subsumes the negative floor: the
  privileged keys `system.admin`, `user.manage`, and `authz.assignment.write` are
  rejected because they are not members of the authorable set, as is any unknown
  or mistyped key. One mechanism, not an allow-list plus a parallel deny-list.
- Update and delete reject `is_system = 1` rows (`Invalid`): seeded roles are
  read-only. A rejected update writes nothing and leaves the existing row, its
  permission rows, and the audit trail unchanged (the audit row is inside the
  mutation transaction, D8, so a rejected write that never commits leaves no audit
  row).

EE gets presentation metadata only (D5), so the enforced allow-list is never
sourced solely from `Freeboard.Enterprise`. Defence in depth: even if the
entitlement gate and the EE catalog were bypassed, the MIT store never mints a
privilege-escalating or malformed role.

Alternative considered (Plan A/D5): EE owns the authorable-key catalog and the
store keeps only a negative deny-list. Rejected: it makes a security-relevant
allow-list sourced only from the paid assembly and forces the store to encode a
parallel deny-list; the positive allow-list in Core is both safer and smaller.

### D4. Reserved `role_key` prefix `custom:` plus a bounded slug

Source: A's prefix, B's slug discipline; B's `:org:` middle segment rejected.
Seeded keys carry no colon, so a `custom:` prefix cannot collide with a current
seed and reserves the namespace against future seeds (seeds must never use it).
The remainder after the prefix is a bounded lowercase ASCII hyphenated slug so
the full key fits `VARCHAR(64)`, is a safe URL route segment
(`/custom-roles/{roleKey}`), and cannot shadow a seeded key. Colon is valid in
the `utf8mb4_bin` column and absent from every existing role key.

Rejected (Plan B): `custom:org:<slug>`. The `:org:` segment encodes a scope that
is already a hard invariant (custom roles are always `organisation`-scoped, D-non
goal), so it is redundant, and its only stated purpose is a future scope family
that is an explicit non-goal. Encoding a speculative future taxonomy in a
persisted key is a liability; if a second scope family ever ships it can add its
own prefix then. Rejected (Plan A alternative): a separate `origin` column;
that is a schema change (D1) for something a naming rule expresses for free.

### D5. EE carve-out is the presentation catalog over the Core allow-list

Source: A (EE holds the catalog) narrowed by B (presentation only). The paid
feature is the authoring surface, gated by `CustomPolicies`. `Freeboard.Enterprise`
holds a presentation catalog: for each key in
`AuthzCustomRoles.AuthorablePermissionKeys`, a human label, description, and
group, for the designer page and the API's option list. It references
`Freeboard.Core` only (its `.csproj` already references just Core) and no web or
persistence type. The catalog may only annotate keys already in the Core
allow-list; it can never widen the enforced set. The web endpoint validates the
submitted set against the Core allow-list (the security check), and renders labels
from the EE catalog (the presentation concern). This is the only new EE type; its
liability is justified by the Epic-1 carve-out requirement. The catalog type is
`public`: the web project (`Freeboard`) consumes it directly, and the web test
project reaches it transitively through `Freeboard` (the tests reference Enterprise
only through the web app), so an `internal` type would not be visible to either.

### D6. Entitlement gate via a reusable endpoint filter, routes always mapped

Source: both agree (A/D6). Add `RequireEntitlement(EnterpriseEntitlement)` in the
web project (mirrors `RequirePermission`): an endpoint filter that resolves
`IEnterpriseEntitlements` from request services and short-circuits with 404 when
not entitled. Applied to the custom-roles route group AHEAD of the permission
filter, so an unentitled install returns 404 for the whole surface even to a
super-admin (feature absent, not forbidden). The Razor page performs the same
check at the top of each handler, returning `NotFound()`.

Both filters run at endpoint execution, AFTER `GitOpsReadOnlyMiddleware`
(`Program.cs` runs `UseRouting`, then the middleware, then maps endpoints). So
under GitOps read-only a mutating request is rejected with 409 before the
entitlement filter runs; the 404-when-off guarantee applies when read-only is off
and to GET requests (which the middleware does not intercept). See D12.

Routes are mapped unconditionally (the filter, not conditional mapping, does the
gating) so `RouteAuthzMetadataTests` can see the endpoints and their force-enforce
metadata under the default (gate-off) test host. Alternative considered:
conditionally mapping routes when entitled; rejected because it hides the routes
from the metadata test and splits wiring across two code paths.

### D7. Super-admin gate plus force-enforce on every mutation

Source: both agree (B H2 aligns with A/D7). Authoring global role definitions is a
system operation. `authz.assignment.write` is deliberately not enough: a role
definition shapes future delegation globally. The route group uses
`RequirePermission(AuthzActions.SystemAdmin, SystemSelector, alwaysEnforce: true)`
(the same selector `RoleAssignmentEndpoints` uses for system routes). Each mutating
route therefore carries `AuthzPermissionMetadata { Action = system.admin,
AlwaysEnforce = true }`. The Razor page handlers call `AuthzPageGuard.CheckAsync(...,
AuthzActions.SystemAdmin, new AuthzResource("system", null, null, []))`, which always
enforces. New `RouteAuthzMetadataTests` `InlineData` rows cover POST/PUT/DELETE
`custom-roles`; the existing universal guard `EveryMutatingApiRouteIsGatedOrAllowlisted`
already fails any new mutating route that is neither force-enforced nor
allowlisted.

### D8. Audit every mutation atomically, inside the write transaction

Source: B (M1), adopted over A. The real audit path is out-of-band:
`AppendAuditEventAsync` opens its own connection and `AuthzMutationAudit.AppendAsync`
swallows failures behind the logger. Best-effort cannot satisfy "every mutation
writes an audit row": a committed create could lack its audit row if the separate
append fails. So each role write inserts the `authz_audit_events` row INSIDE the
same transaction as the role mutation, using a private helper that takes the open
transaction. A commit carries both the mutation and its audit row; a rollback
drops both.

- The three write methods take the actor user id (and derive event metadata):
  event types `authz.role.create`, `authz.role.update`, `authz.role.delete`;
  `resource_type = "authz_role"`, `resource_id = role_key`, `effect = "Permit"`.
- The existing assignment routes keep their best-effort `AuthzMutationAudit` path
  unchanged; this change does not rewrite them (smallest coherent change). The
  existing `AppendAuditEventAsync` method stays for those callers.

Alternative considered (Plan A/D8): call `AuthzMutationAudit.AppendAsync` after
the store returns `Ok`. Rejected: it is best-effort and out-of-band, so a
successful mutation can lack an audit row, contradicting the requirement.

### D9. In-use-delete protection, race-safe

Source: both agree; B (M2) adds race-safety. `DeleteCustomRoleAsync` locks the
target role row and counts rows in `authz_organisation_role_assignments` (and,
defensively, `authz_system_role_assignments`) for the `role_key` inside the delete
transaction; a non-zero count returns `Conflict` ("role has live assignments") and
deletes nothing. The `ON DELETE RESTRICT` FK from the assignment tables to
`authz_roles` is the DB backstop; a delete that races an assignment insert and
trips the FK is caught and mapped to `Conflict` (409) rather than surfacing a raw
FK exception. A delete of an unused role removes the role and cascades its
`authz_role_permissions` rows (FK `ON DELETE CASCADE`).

### D10. `role_key` is immutable after create

Source: B. `role_key` is the PK and the `ON DELETE RESTRICT` FK target from both
assignment tables; renaming it would orphan or require cascade-updating live
assignments. Update changes `title`, `description`, and the permission set only;
it never changes `role_key`, `scope`, or `is_system`. The request body cannot set
`scope` or `is_system` (create always writes `organisation` / `0`), and a `role_key`
in an update body is ignored in favour of the route value. Renaming a role is
delete-then-recreate while it is unused.

### D11. Web surface shape

Source: synthesis of Plan A's and Plan B's route names, grounded in the existing
route naming
(`system-role-assignments`, `organisations/{orgId}/role-assignments`: descriptive
kebab-case, no `authz/` segment). Chosen: `custom-roles` (descriptive, scoped to
what it manages, avoids implying it governs all roles).

- API (minimal API under `/api/v1/freeboard`, new `CustomRoleEndpoints`):
  `GET /custom-roles`, `GET /custom-roles/{roleKey}`, `POST /custom-roles`,
  `PUT /custom-roles/{roleKey}`, `DELETE /custom-roles/{roleKey}`. Group carries
  `RequireEntitlement(CustomPolicies)` then `RequirePermission(SystemAdmin,
  SystemSelector, alwaysEnforce: true)`. Request bodies use snake_case JSON
  (`role_key`, `title`, `description`, `permission_keys`) matching
  `RoleAssignmentEndpoints`. The endpoint validates the submitted set against the
  Core allow-list, plus `title`/`description` widths, before calling the store; the
  store validates again (defence in depth) and writes the audit row (D8).
- Shared helpers: `SystemSelector`, `ValidationProblem`, and `Conflict` are today
  `private static` in `RoleAssignmentEndpoints`, so `CustomRoleEndpoints` cannot
  reference them directly. `SystemSelector` is identical and security-relevant (the
  `system` resource), so it is extracted once into a shared internal helper in the
  `Freeboard.Authz` namespace that both endpoint classes call. The `ValidationProblem`
  (422) and `Conflict` (409) responders differ only in their problem title
  ("Invalid role assignment" vs a custom-role title), so `CustomRoleEndpoints`
  keeps its own small local copies with custom-role titles rather than sharing a
  parameterised helper; these are distinct problem responses, not one shared behaviour.
- Page (`/admin/custom-roles`, `CustomRolesModel` + `CustomRoles.cshtml`): SSR list
  of custom roles with a create form (checkbox list from the EE presentation
  catalog), per-role edit (title, description, permissions), and a delete button.
  Handlers gate on entitlement (`NotFound()` when off) then `AuthzPageGuard` for
  `system.admin`, call the same store the API uses, and set a `Notice`. The
  `/Admin` folder authorization already applies, so no extra page registration is
  needed; unlike `/Admin/RoleAssignments`, the page is NOT added to the
  `AuthEndpoint` conventions block, so GitOps read-only blocks its POST (D12),
  consistent with the API.
- Nav: `_Layout.cshtml` shows the Custom Roles link only when `isAdmin` and
  `IsEntitled(CustomPolicies)`.

### D12. GitOps read-only blocks custom-role authoring (page and API)

`GitOpsReadOnlyMiddleware` rejects mutating methods with 409 unless the matched
endpoint carries the `AuthEndpoint` marker (`GitOpsReadOnlyMiddleware.cs`, the
`GetMetadata<AuthEndpoint>()` check). The role-assignment precedent is split: the
`/Admin/RoleAssignments` PAGE is exempt (listed in the `AuthEndpoint` conventions
block in `Program.cs`), but the role-assignment API (`RoleAssignmentEndpoints`)
carries no `MarkAuthEndpoint()`, so it is blocked (409) under read-only. That
page/API split is not replicated here.

Decision: the custom-role authoring page and API are BOTH blocked under GitOps
read-only. Neither the `/Admin/CustomRoles` page nor the `CustomRoleEndpoints`
routes carry the `AuthEndpoint` marker, so the middleware's default 409 applies to
their mutations, and the page and API are consistent with each other. This 409
precedes the entitlement filter (D6): the middleware runs after `UseRouting` but
before endpoint filters execute, so a mutating custom-role request under read-only
returns 409 regardless of entitlement state. The entitlement-off 404 therefore only
applies when read-only is off, or to GET requests (which the middleware does not
intercept). Rationale:
authoring a role definition is policy configuration, which on a GitOps-managed
instance belongs in the git repository; the `AuthEndpoint` exemption is reserved
for operational access and credential actions that must survive read-only (login,
logout, password reset, user enable/disable, assigning an existing role), not for
minting new policy. Blocking is also the lowest-liability choice: it needs no
marker wiring, only a test asserting the 409.

## Risks / Trade-offs

- [Privilege escalation via an authorable key] -> The enforced allow-list lives
  in `Freeboard.Core` and is applied by the MIT store (D3), not only in the EE
  catalog or the UI, and is covered by a MySQL integration test; the entitlement
  gate is an availability control, not the security control.
- [Force-enforce forgotten on a new route] -> `RouteAuthzMetadataTests` universal
  guard fails any ungated/mode-relaxable mutating API route; new `InlineData` rows
  pin the specific `custom-roles` routes.
- [Audit row missing after a mutation] -> The audit row is written in the same
  transaction as the mutation (D8), so a committed mutation always carries its
  audit row.
- [Entitlement flip leaves stale custom roles] -> Acceptable and intended: seeded
  and custom roles keep working when the gate is off; only authoring is withdrawn.
  Deleting a custom role while assignments exist is blocked (D9).
- [role_key collision with a future seed] -> The reserved `custom:` prefix (D4)
  fences custom keys off; seeds must never use it.
- [EE reference leak] -> The catalog references `Freeboard.Core` only. The existing
  `EnterpriseReferenceTests`/`AuthzPlacementTests`/`EntitlementPlacementTests` pin
  the reverse direction (Agent/CLI/Core carry no Enterprise reference) but none
  asserts the forward direction, that `Freeboard.Enterprise` references only
  `Freeboard.Core` and nothing web or persistence. A new assertion in
  `EnterpriseReferenceTests` pins it by parsing `Freeboard.Enterprise.csproj` and
  asserting its only `ProjectReference` is `Freeboard.Core` (task 3.x).

## Migration Plan

No database migration. Deploy is code-only. Roll out with `Enterprise:
CustomPolicies` unset (default off): endpoints and page return 404, no behaviour
changes. Enabling the config key turns on authoring. Rollback is the config flip or
a code revert; custom roles already authored remain valid data and continue to
enforce (they are ordinary `authz_roles` rows), and can be removed via the API/page
while entitled, or directly in the DB.

## Open Questions

- Title uniqueness: the plan keys on `role_key` (PK) and allows duplicate display
  titles. Confirm no product need for unique titles.
- Whether the `/admin/custom-roles` page should also list seeded roles read-only
  for reference, or only custom roles. The plan lists custom roles for editing and
  can show seeded roles as non-editable context cheaply; confirm preference.
