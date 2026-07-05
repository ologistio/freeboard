## Context

Today the web app has two authorization gates, both registered in `Program.cs`
via `AddAuthorizationBuilder()`:

- `RequireAuthorization()` - any authenticated bearer principal. Used for
  compliance reads and account endpoints.
- `RequireAuthorization(GlobalRoles.AdminPolicy)` - `RequireClaim(freeboard:role,
  admin)`. Used for compliance writes (`ComplianceWriteEndpoints`) and user
  administration (`UserAdminEndpoints`).

The authenticated principal is a single-identity `ClaimsPrincipal` built by
`BearerAuthenticationHandler` carrying `freeboard:user_id` (a ULID, `CHAR(26)`),
`freeboard:session_id`, `freeboard:role` (global `admin`/`member`), and
`freeboard:auth_state` (0 = full, 1 = force-reset-limited). Razor Pages are gated
by the `PageChallenge` scheme conventions; pipeline authorization handlers do not
run for in-process page handlers, so page-level checks are done inside handlers
(as `AdminGuard` and `SudoRecency` already do).

The domain is an organisation tree: `organisations(id VARCHAR(190) utf8mb4_bin,
kind, parent_id self-FK ON DELETE RESTRICT)`, read as
`OrganisationRow(Id, Title, Kind, Parent?)`. Hierarchy is walked in memory in C#
(there is no recursive CTE): `StatementOfApplicability.Resolve` does a
nearest-ancestor parent-walk with a visited-set cycle guard; `OrgScope.InScopeIds`
does a stack-based descendant DFS. `IOrgAccess` (default `AllOrgAccess`) is the
single seam that returns the org ids a user may access; today it returns all of
them, and `OrgSelectionResolver` plus the SoA page already route their accessible
set through it.

Current read surface: `ComplianceEndpoints` returns full store rows to any
authenticated user for `/organisations`, `/scopes`, `/requirement-scopes`, and the
SoA JSON endpoint; the SoA page filters by `IOrgAccess.AccessibleOrgIds`, which
today returns everything. Current write surface: all six compliance write routes
sit behind `GlobalRoles.AdminPolicy`. `DeleteOrganisationAsync` pre-counts child
orgs, scopes, and requirement-scopes to front-run `ON DELETE RESTRICT`;
`MySqlGitOpsImporter.DeleteAbsentOrganisationsAsync` deletes absent orgs
leaf-first under the same RESTRICT constraint. `MySqlUserStore.TryBootstrapAdminAsync`
creates the first admin, its password credential, and the `bootstrap_marker` in
one `ReadCommitted` transaction. `UserAdminEndpoints.DisableUserAsync` has no
last-admin or self-disable guard.

Persistence conventions: read store (`IComplianceStore`) and write store
(`IComplianceWriteStore`, methods return `WriteResult`) are split; multi-table
reads use a `RepeatableRead` transaction and stitch rows in memory to avoid N+1;
deletes pre-count referencing rows to front-run `ON DELETE RESTRICT`. Migrations
are embedded `Migrations/NNN_slug.sql` resources auto-discovered and ordered by
ordinal; the latest is `009`, so the next is `010`. Ids and FK columns use
`utf8mb4_bin`. Stores are registered as singletons through role-split `Add*`
extension methods that share one connection factory. Web tests inject hand-written
fakes via `ConfigureTestServices`; MySQL integration tests are gated on
`FREEBOARD_TEST_DB`.

## Goals / Non-Goals

**Goals:**

- One small decision seam the whole app calls, with feature/role logic layered
  above it.
- A policy engine that is pure, deny-by-default, and unit-testable without a
  database.
- Roles and permission sets stored as data so custom roles and an EE role designer
  are a later addition, not a schema rewrite.
- Per-org RBAC that resolves through the org hierarchy (a grant on an org covers
  its subtree).
- Compliance reads narrowed to the caller's authorized subtree in this increment,
  so per-org authorization is a real boundary.
- Enforcement that fails closed, returns RFC 7807 problems, and audits denied
  decisions and mutations.
- A staged, non-breaking rollout via a named mode; no currently-permitted request
  becomes denied when shipped in Compat.
- Everything MIT; respect the one-way EE rule and keep Agent/CLI authz-free.

**Non-Goals:**

- A user-authored policy DSL or a general ReBAC graph engine (arbitrary tuple
  types, userset rewrites, Zanzibar-style relations). The relationship set is
  bounded and known.
- A custom-role editor UI in this increment. The four roles ship seeded; the
  schema supports custom roles, but authoring them is a follow-up (an EE surface).
- Attribute-condition policies beyond authentication, action, and session
  step-up/limited state (for example "only during business hours", "only from IP
  range").
- Row-level ABAC on individual scope/requirement records beyond their owning
  organisation.
- Narrowing the non-tenant catalog reads (standards, controls, requirements).
  These are shared reference data with no confidentiality boundary and stay
  authenticated-only.
- SCIM provisioning and CLI role commands. These are deliberate follow-ups;
  enforcement is web-only.

## Decisions

The decisions are numbered D1..D12 and cross-referenced from the Provenance
section. Each states the unified choice; Provenance records where it came from.

### D1: A two-layer decision seam - a pure Core engine and a web authorizer

The core decision lives in `Freeboard.Core/Authz` and is pure:

```csharp
public interface IAuthorizationEngine
{
    AuthzDecision Evaluate(AuthzRequest request);
}
```

- `AuthzRequest` is immutable: the `AuthzPrincipal` (user id, is-authenticated,
  session step-up and limited flags, the principal's effective system permissions,
  and the principal's org grants as `(permissionKey, organisationId)` facts), the
  `AuthzAction` (a dotted string key), and the `AuthzResource` (type, optional id,
  optional organisation id, and the inclusive org-ancestry chain).
- `AuthzDecision` is `(AuthzEffect Effect, string Reason)` where effect is `Deny`
  or `Permit`. `Reason` is for audit/debugging, never shown to the user.

The app-facing seam is `IAuthorizer` in the web project (where `ClaimsPrincipal`,
the org tree, the store, and the rollout mode live):

```csharp
public interface IAuthorizer
{
    ValueTask<AuthzDecision> AuthorizeAsync(
        ClaimsPrincipal user, string action, AuthzResource resource, CancellationToken ct);
}
```

The default `Authorizer` builds the principal via `ClaimsPrincipalAuthzPrincipalFactory`,
loads the principal's facts once per request via the read store, resolves the
resource org's inclusive ancestry from the org tree, evaluates the engine, applies
the rollout mode (D11), and audits (D9). It fails closed: any exception resolves to
`Deny`.

Because the per-request fact/grant load is per-user state, the `Authorizer`, its
grant/fact cache, and `AuthzOrgAccess` (D10) are registered SCOPED, not singleton.
They share ONE request-scoped cache so a principal's grants are loaded at most once
per request and are never cached across requests (a singleton could not hold this
state without leaking one user's grants into another's request). This is a
deliberate lifetime change from the current singleton `AllOrgAccess` registration.

Rationale: the engine stays pure and I/O-free, so policy logic is fully
unit-testable without MySQL or ASP.NET. `Freeboard.Core` referencing only BCL
types keeps the reference graph intact. The fact-loading port (`IAuthzFactProvider`)
is a Core interface implemented by the web authorizer over the Persistence store,
matching the "store interface in Persistence, seam wiring in Web" pattern.

### D2: Roles and permission sets are data, loaded via fact providers

Roles and their permission sets are rows in `authz_roles`, `authz_permissions`,
and `authz_role_permissions` (D8), seeded by migration 010. The engine never
hard-codes a role-to-permission map; it evaluates against the principal's
effective permissions as loaded facts. This is what lets custom or tenant-defined
roles and an EE role designer be added later by inserting rows, not by editing and
redeploying code.

Action-identifier strings (the eight permission keys) remain code constants in
`Freeboard.Core` (`AuthzActions`) because endpoints reference them at call sites
(`RequirePermission(AuthzActions.ComplianceScopeWrite, ...)`). The distinction is
deliberate: an action key is a compile-time contract between an endpoint and the
engine; a role's permission set is operational data.

### D3: Policy model is a pragmatic hybrid over persisted facts

The engine runs an ordered set of `IAuthzPolicy` contributors; each returns
`Permit`, `Deny`, or `NotApplicable`. Combining is deny-overrides: any `Deny`
wins; else any `Permit` wins; else the default is `Deny` (fail closed). v1 ships,
in order:

1. `SessionGuardPolicy` (ABAC hard deny): an unauthenticated principal, or a
   principal whose session is force-reset-limited, is denied for any action
   outside the limited-session allowlist. This is a hard-deny input, evaluated
   first, so a limited or anonymous caller can never be permitted by a later
   policy.
2. `SystemAdminPolicy` (attribute): a principal holding `system.admin` is
   permitted every action. This is the break-glass super-admin and preserves every
   current admin gate.
3. `SelfAccessPolicy` (attribute seam): the ordered slot for self-service ABAC
   rules (a principal acting on its own `user` resource). v1 ships it as the named
   extension point with no default rule, because current self-service endpoints are
   gated by session state, not authz; adding a rule later needs no seam change.
4. `OrgRbacPolicy` (relationship): `Permit` when the principal holds a grant whose
   organisation is in the resource's inclusive ancestry and whose effective
   permissions contain the action.

Rationale: Freeboard's real relationships are exactly two - "user has role in org"
and "org descendant-of org" - and its attributes are authentication, the action,
and session state. A Zanzibar-style engine is unjustified liability against a
bounded requirement. The ordered deny-overrides pipeline expresses RBAC now and
admits attribute policies later without changing the seam.

### D4: Per-org RBAC resolves through the org subtree by inclusive-ancestor match

- A role assignment is `(user_id, role_key, organisation_id)`. It grants the role
  on that organisation and its entire subtree.
- Resolution: the authorizer builds the resource org's FULL inclusive ancestry
  `[R, parent(R), ..., root]` with an in-memory parent-walk guarded by a visited
  set against cycles. `StatementOfApplicability` walks the same parent chain with the
  same cycle guard, but consumes it differently: it SHORT-CIRCUITS at the nearest
  ancestor that has a disposition, so it does not build the full chain, whereas the
  authorizer needs the WHOLE chain to match any granting ancestor. To keep the
  cycle-guard behaviour (which RBAC correctness depends on) from diverging, extract
  the inclusive-ancestry BUILD shared by both callers into ONE helper (a pure function
  over the org list): the authorizer uses the full chain as-is, and the SoA projection
  consumes the same chain but stops at the first node with a disposition. This is an
  extraction of the shared build logic, not the reuse of a single existing private
  helper. `OrgRbacPolicy` permits the action if any grant's organisation is in that
  ancestry and the grant's role permits the action. Grant-on-ancestor covers
  descendant resources; this is the subtree rule.
- Department scoping is just an assignment at the department node. `OrgScope.InScopeIds`
  remains the UI/list scoping primitive, but its accessible set now comes from
  authz (D10) instead of `AllOrgAccess`.

### D5: The role set and permission-to-endpoint mapping

Four seeded roles (all `is_system=1`). A role's scope is a PERSISTED invariant: the
`authz_roles.scope` column (`system` | `organisation`) records which assignment table
a role may be written to, so scope is data the store validates, not a naming
convention. `super-admin` is seeded `system`; the three org roles are seeded
`organisation`. The write store rejects a cross-scope grant (a `system` role through
the org-scoped assignment endpoint, or an `organisation` role through the system
assignment endpoint) with the validation-failure status (422/400), and the fact
loader defensively ignores a mis-scoped assignment row so a stray row can never grant
permit-all:

- `super-admin` (scope `system`, held via `authz_system_role_assignments`):
  permission `system.admin`. The engine treats `system.admin` as permit-all.
- `org-owner` (scope `organisation`): `org.read`, `org.write`, `compliance.read`,
  `compliance.scope.write`, `compliance.requirement-scope.write`,
  `authz.assignment.write`.
- `compliance-manager` (scope `organisation`): `org.read`, `compliance.read`,
  `compliance.scope.write`, `compliance.requirement-scope.write`.
- `compliance-reader` (scope `organisation`): `org.read`, `compliance.read`.

Eight permission keys mapped to the actual endpoints:

- `org.read` -> `GET /organisations` (list narrowed to the accessible set).
- `compliance.read` -> `GET /scopes`, `GET /requirement-scopes`, the SoA JSON
  endpoint and page (all narrowed by organisation).
- `org.write` -> `PUT`/`DELETE /organisations/{id}`.
- `compliance.scope.write` -> `PUT`/`DELETE /scopes/{id}`.
- `compliance.requirement-scope.write` -> `PUT`/`DELETE /requirement-scopes/{id}`.
- `authz.assignment.write` -> the org-scoped role-assignment management endpoints.
- `user.manage` -> `UserAdminEndpoints` (`/users*`), the admin user-management Razor
  pages (`Pages/Admin/Users` OnGet/Create/Disable/Enable/ResetPassword and
  `Pages/Admin/UserCredential` OnGet, gated in-handler by `AuthzPageGuard`), AND the
  cross-user branch of the `AuthEndpoints` session routes
  (`GET`/`DELETE /auth/sessions/{id}`, `GET`/`DELETE /users/{id}/sessions`). Held by
  no seeded role; only reachable through `system.admin`, so user administration and
  cross-user session management stay super-admin-only and behavior is unchanged. The
  two admin pages are pure admin surfaces with no self-service branch; they move off
  `AdminGuard` (which reads the legacy `freeboard:role=admin` claim) onto an
  `AuthzPageGuard` `user.manage` check, and `AdminGuard` is deleted because these are
  its only callers, so the legacy admin claim grants nothing on those pages. These session routes are
  dual-purpose: a caller acting on its OWN sessions is permitted self-only (the
  existing owner-id match, no permission required and unchanged), while acting on
  ANOTHER user's sessions is the cross-user branch. Today that branch derives from
  `IsAdmin` (the `freeboard:role=admin` claim) via `callerIsAdmin` in `AuthFlows`;
  it now derives from an `IAuthorizer` `user.manage` decision on the target `user`
  resource instead, so the legacy admin claim grants NOTHING on them - a revoked
  super-admin holding only the stale claim can no longer list or revoke another
  user's sessions. The self-service page handlers (`Pages/Account/Sessions`,
  `SessionsRevoke`) already pass `callerIsAdmin: false`, so they are self-only and
  unaffected. The admin nav link in `Pages/Home.cshtml` and
  `Pages/Shared/_Layout.cshtml` follows this boundary too: it is shown when the
  principal can reach the admin surface (holds `user.manage` or `system.admin`, from
  the loaded permissions / the authorizer), not when it merely carries the legacy
  `freeboard:role=admin` claim. This is a cosmetic affordance and fail-safe (hide on
  any error); the pages themselves force-enforce, so it is not a security control.
- `system.admin` -> the super-admin permit-all and the system-role assignment
  endpoints.

Catalog reads (`/standards`, `/controls`, `/requirements`) carry no organisation
and stay authenticated-only; they are shared reference data with no
confidentiality boundary, so narrowing them would deny reference data to a scoped
user for no security gain.

### D6: Principal and resource modelling ties to the existing claims and org ids

- Principal = the authenticated user. Id from `freeboard:user_id`; system
  permissions and org grants loaded from the store; `IsAuthenticated` and the
  session step-up/limited flags (`freeboard:auth_state`) as attributes. No new
  identity concept, and the limited-session flag is a hard-deny input (D3.1).
- `AuthzResource(string Type, string? Id, string? OrganisationId,
  IReadOnlyList<string> OrgAncestryInclusive)`. Types this increment:
  `organisation`, `scope`, `requirement_scope`, `user`. For a `scope` or
  `requirement_scope`, the owning organisation id determines the ancestry.

### D7: Enforcement is opt-in per endpoint, fails closed, and uses RFC 7807

- Minimal APIs: a `RequirePermission(action, resourceSelector, alwaysEnforce = false)`
  endpoint-filter extension (`AuthzEndpointExtensions` / `AuthzEndpointFilter`). The
  selector extracts the resource and its org id from the route or body; the filter
  calls `IAuthorizer` and, on `Deny`, short-circuits with a problem response. A
  missing or throwing selector is treated as `Deny`. The `Authz:Mode` relaxation (D11)
  governs READS only: a mode-relaxed `Deny` on a read does not block in `Observe`, and
  `Compat` applies the zero-grant read fallback. EVERY mutating route - compliance
  writes, user-admin mutations, and role-assignment management - sets
  `alwaysEnforce: true`, so its `Deny` blocks in EVERY mode (`Observe`, `Compat`,
  `Enforce`); there is no non-blocking write in any mode. The authorizer exposes a
  mode-independent decision path for this (an always-block `AuthorizeAsync` overload
  or flag). Writes are admin-gated today, so force-enforcing them from day one drops
  nothing below the current gate: the migration super-admin backfill plus the
  admin-create fold-in preserve every current writer. `AuthzPageGuard` likewise always
  blocks for sensitive page handlers.
- A PUT is an upsert, and the compliance write store uses `ON DUPLICATE KEY UPDATE`
  that can change `organisation_id` (scopes, requirement-scopes) or `parent_id`
  (organisations). A selector that authorizes only the requested new org would let
  a caller with write on org A overwrite or MOVE a row currently owned by org B. A
  PUT on an EXISTING row SHALL therefore authorize BOTH the stored row's current
  owning org AND the requested new org (the caller must hold the write permission on
  each). The selector reads the stored row's current owning org before the write.
  Organisation PUT has its own rules because an organisation's "owning org" is its
  parent: creating a ROOT organisation (no parent) requires `system.admin`; creating
  a CHILD organisation authorizes `org.write` on the parent; reparenting an existing
  organisation authorizes `org.write` on BOTH the current parent and the new parent.
- Response shape: `Deny` on an action against a visible resource returns 403 with
  an RFC 7807 body (matching the existing 503/409/422 style). For an org-scoped
  resource the principal cannot see at all, the selector returns 404 (existence
  non-disclosure). The problem body never contains the decision reason.
- Razor Pages: sensitive handlers call `IAuthorizer` directly via `AuthzPageGuard`
  (replacing `AdminGuard`), returning a bare 403 or 404, because pipeline policies
  do not run for in-process handlers. This includes the admin user-management pages
  `Pages/Admin/Users` (OnGet plus the Create/Disable/Enable/ResetPassword handlers)
  and `Pages/Admin/UserCredential` (OnGet), which today gate on `AdminGuard`
  (the legacy `freeboard:role=admin` claim) and mutate by calling stores/flows
  directly, so neither the `RequirePermission` filter nor the route-metadata test
  reaches them; each handler now calls `AuthzPageGuard` for `user.manage`. Because the
  gate is in-handler, it is guaranteed by behavioral web tests (the page analogue of
  the session-route tests), NOT by the route-metadata test.
- Dual-purpose cross-user session routes: the four `AuthEndpoints` session routes
  (`GET`/`DELETE /auth/sessions/{id}`, `GET`/`DELETE /users/{id}/sessions`) serve a
  self-service path AND a cross-user admin branch, so a whole-route
  `RequirePermission` filter (which blocks the entire route on `Deny`) cannot be
  used without breaking self-service. The cross-user branch is instead force-enforced
  by an in-handler `IAuthorizer` call for `user.manage` on the target `user` resource,
  the same mechanism `AuthzPageGuard` uses: it always blocks a `Deny` in every mode
  (the mode relaxation governs reads only, and these are privileged cross-user
  reads/mutations, not the org-scoped compliance reads the Compat fallback covers),
  and the decision is audited. The self path (owner-id match) needs no permission and
  is unchanged. Because there is no route filter to inspect, each cross-user session
  route carries endpoint metadata declaring its required cross-user permission
  (`user.manage`), so the route-metadata architecture test (D7 fail-closed bullet)
  records that these routes are declared authz-gated. That metadata assertion only
  proves the annotation is PRESENT; it cannot prove the in-handler check still runs.
  A dropped in-handler check (annotation left in place, authorizer call removed) is
  caught by the BEHAVIORAL web tests (task 4.4b), not by the metadata test.
- Fail-closed is a property of the engine and filter, not of a forgotten route: the
  engine defaults to `Deny` and the filter denies on any missing/throwing selector,
  so once a route opts in, an incomplete check denies rather than leaks. But a route
  with NO `RequirePermission` filter still runs its handler untouched - the runtime
  default-deny only applies when the filter is actually invoked. The real guard
  against a forgotten filter is therefore a metadata/architecture test, not runtime
  behaviour: an architecture/integration test inspects route metadata and asserts
  every mutating compliance, role-assignment, AND user-admin FILTER-GATED minimal-API
  route carries a permission requirement and `alwaysEnforce: true`. The metadata test
  covers filter-gated minimal-API routes only. For the in-handler gates - the
  `AuthzPageGuard` on the admin user-management pages and the dual-purpose session
  routes - the test can at most assert the declared cross-user permission metadata
  (`user.manage`) is present; it CANNOT verify the in-handler authorizer call still
  runs, so those in-handler gates are guaranteed by behavioral web tests (tasks 4.4b
  and the admin-page tests), not by the metadata test. That metadata test, not
  "fail closed", is what stops an ungated filter-based mutating endpoint from shipping.

### D8: Persistence is six data-driven tables, migration 010, and a store pair

`src/Freeboard.Persistence/Migrations/010_authorization.sql` creates, with
`utf8mb4_bin` ids and FK columns:

- `authz_roles(role_key PK, title, description, scope, is_system, created_at,
  updated_at)`, where `scope` is `system` or `organisation` and constrains which
  assignment table the role may be written to.
- `authz_permissions(permission_key PK, resource_type, description, is_system)`.
- `authz_role_permissions(role_key, permission_key, PK(role_key, permission_key),
  FK role_key -> authz_roles ON DELETE CASCADE, FK permission_key ->
  authz_permissions ON DELETE RESTRICT)`.
- `authz_system_role_assignments(user_id, role_key, PK(user_id, role_key),
  FK user_id -> users ON DELETE CASCADE, FK role_key -> authz_roles ON DELETE
  RESTRICT, created_at, updated_at)`.
- `authz_organisation_role_assignments(user_id, role_key, organisation_id,
  PK(user_id, role_key, organisation_id), FK user_id -> users ON DELETE CASCADE,
  FK role_key -> authz_roles ON DELETE RESTRICT, FK organisation_id ->
  organisations ON DELETE RESTRICT, created_at, updated_at)`.
- `authz_audit_events(id PK, occurred_at, event_type, actor_user_id, action,
  resource_type, resource_id, organisation_id, effect, reason)` with scalar
  actor/resource ids and no strict foreign keys, so audit history survives user
  and organisation deletes (D9).

The migration seeds the four roles with their `scope` (`super-admin`=`system`, the
three org roles=`organisation`), the eight permissions, and the role-to-permission
rows (`is_system=1`), backfills `super-admin` system assignments
from `users` where `global_role='admin'`, and backfills existing enabled non-admin
users to `compliance-reader` on the current root organisations (D11, the member
backfill).

The migration MUST be idempotent/re-runnable, because the migration runner
re-attempts a migration that failed partway (its version stays unrecorded on a
partial failure, so a re-run replays the whole file). Every statement therefore
uses a form that is safe to replay: `CREATE TABLE IF NOT EXISTS` for all six
tables, and `INSERT ... ON DUPLICATE KEY UPDATE` (or `INSERT IGNORE`) for every
seed and backfill row - roles, permissions, role-permissions, the super-admin
backfill, and the member backfill. This mirrors the existing re-runnable-migration
convention.

Organisation foreign key is `ON DELETE RESTRICT`, matching the schema convention
(007, 009). It is NOT `CASCADE`: an org delete is guarded, not silent. The
org-delete path and the GitOps importer prune `authz_organisation_role_assignments`
for the affected org before deleting the organisation (D12), the same prune-before-
delete pattern already used for scopes and requirement-scopes.

Stores mirror the compliance split:

- `IAuthzStore` (read): `LoadPrincipalFactsAsync(userId, ct)` returning the
  principal's system permissions and org grants (each as effective
  `(permissionKey, organisationId)`), plus `ListSystemAssignmentsAsync` and
  `ListOrganisationAssignmentsAsync(orgId, ct)` for the management UI. Fact
  loading is bounded: two queries (system assignments joined to role permissions,
  org assignments joined to role permissions), no per-request N+1. Reads that span
  multiple tables for one decision use a `RepeatableRead` snapshot. Both fact queries
  join `authz_roles.scope` and defensively DROP a mis-scoped row (an `organisation`
  role found in the system assignment table, or a `system` role in an org assignment
  row), so a stray row from a schema breach can never contribute `system.admin`
  (permit-all) to the principal's facts.
- `IAuthzAdministrationStore` (write): `AssignSystemRoleAsync`,
  `RevokeSystemRoleAsync`, `AssignOrganisationRoleAsync`,
  `RevokeOrganisationRoleAsync`, and `AppendAuditEventAsync`, all returning
  `WriteResult`. Assign validates the role is known, the user/org exist, AND the
  role's `scope` matches the assignment table - a `system` role through
  `AssignOrganisationRoleAsync` or an `organisation` role through
  `AssignSystemRoleAsync` returns the validation-failure status (422/400) and writes
  nothing, so a mis-scoped grant cannot be persisted. The unique/primary key backstops
  concurrent duplicates (mapped to a 409 like the compliance writes); the
  last-super-admin guard (D12) is enforced here.
- Registration: `AddAuthz(connectionString)` beside `AddAuth`, registering both
  stores as singletons through the shared connection factory. A `FakeAuthzStore`
  in `Freeboard.Web.Tests` mirrors `FakeComplianceStore`.

### D9: Audit is a minimal persistent table plus ILogger

Every decision and mutation is logged via `ILogger` with a stable event id and
structured fields (actor id, action, resource type/id/org, effect, reason); denies
at warning, permits at debug. In addition, `authz_audit_events` persists exactly this
security-relevant set, with scalar ids and no strict FKs so history survives the
deletion of the actor or the referenced resource:

- every denied decision;
- every zero-grant Compat READ served through the legacy read fallback (D11) - it is
  the security-relevant exposure that the Enforce flip closes, so the trail must show
  WHO used the legacy bridge and for what;
- all authz/user/admin mutations (role-assignment writes, and user-admin
  create/disable/enable/reset).

An ordinary PERMIT (a normally-authorized allow) is ILogger-only, not persisted.
Compliance DATA writes are logged only, not persisted to this table. The persisted
set is the security-relevant privilege-and-exposure trail, and the tasks wire exactly
that set - spec and tasks name the same set. The table is intentionally minimal - no
projections, no retention machinery.

Write semantics are precise:

- `ILogger` ALWAYS logs the decision or mutation. Logging is the reliable channel
  and never depends on the authz store.
- The persistent `authz_audit_events` write is BEST-EFFORT. When it fails it is
  skipped and the failure is logged; it never turns a permitted request into an
  error. In particular, when the failed dependency IS the authz store itself (the
  same store the audit row would be written to is unreachable), the persistent write
  is skipped-and-logged rather than retried or thrown - a store outage already
  resolves the decision to `Deny` and is logged, so no second failure is raised.
- In `Observe` mode the authorizer audits the would-be decision (what `Enforce`
  would decide) even though it does not block.

### D10: Compliance reads narrow to the authorized subtree in this increment

Read narrowing ships now, not in a later phase. `AllOrgAccess` is replaced by
`AuthzOrgAccess` as the `IOrgAccess` default:

- `AuthzOrgAccess.AccessibleOrgIds` resolves the accessible set by the rollout
  mode (D11), never narrowing unconditionally. Under `Observe` reads are NOT
  narrowed: it returns ALL persisted organisations for every caller regardless of
  grants, so read behaviour is unchanged while decisions are observed. Under
  `Compat` a grant-holder gets the union of subtrees over which it holds a
  read-granting role (`compliance-reader` or above), computed from the loaded org
  grants and the org tree, and a zero-grant caller gets the audited full read
  fallback; a `super-admin` gets all organisations. Under `Enforce` a grant-holder
  gets the same subtree union (`super-admin` all) and a zero-grant caller gets the
  empty set.
- The selector tree and the SoA page already route their accessible set through
  `IOrgAccess`, so they narrow automatically. The list endpoints `/organisations`,
  `/scopes`, `/requirement-scopes` and the SoA JSON endpoint gain explicit
  filtering to the accessible set (organisations by id; scopes and
  requirement-scopes by owning organisation; SoA resolves over the full tree then
  filters nodes to the accessible subtree, so inherited dispositions survive).
- Because `IOrgAccess` now needs the principal's grants (a store read), its seam
  method becomes async and takes a `CancellationToken`; both call sites
  (`OrgSelectionResolver.GetAsync`, the SoA page `OnGetAsync`) are already async.
  The accessible set is memoized per request alongside the fact load.

A permanent default-allow read policy is explicitly rejected: shipping per-org
authz with unfiltered reads would be a false boundary. Where reads stay open for
legacy callers, that openness is the mode-gated, audited Compat fallback (D11),
which is removed at Enforce - not a permanent policy.

### D11: Rollout is a named mode; the engine is default-deny in all modes

`Authz:Mode = Observe | Compat | Enforce`, read from configuration.

- Observe: mode relaxation applies to ORG-SCOPED COMPLIANCE READS only (the
  accessible-set reads). The engine still decides deny-by-default, but the authorizer
  does not narrow or block a mode-relaxed org-scoped compliance read; it logs and
  audits what Enforce would decide, and those reads are not narrowed (accessible set =
  full) so their behavior is unchanged while decisions are observed. The relaxation
  EXCLUDES the privileged cross-user reads (`GET /auth/sessions/{id}` and
  `GET /users/{id}/sessions`), which force-enforce in every mode (D5, D7); Observe does
  not relax them. Mutating routes still force-enforce (below), so a denied write is
  blocked even in Observe.
- Compat: the authorizer blocks a denied action, but a caller with no assignments
  keeps legacy READ through an explicit fallback - full read only. Each such
  zero-grant fallback READ writes an `authz_audit_events` row (D9), because it is the
  exposure the Enforce flip closes and the trail must record who used the bridge.
  There is
  NO admin-claim write fallback: writes ALWAYS require the proper permission
  (`system.admin`, or the relevant org write permission), in every mode. This is
  safe because no currently-permitted write is lost: today all compliance writes
  require the global admin claim, and the migration backfills those admins to
  `super-admin`, so they still write; non-admins never had write. Making the
  `super-admin` authz assignment the sole source of system power (rather than the
  persistent `freeboard:role=admin` claim that `BearerAuthenticationHandler` sets)
  closes the hole where a revoked super-admin could still write via a stale admin
  claim - the `global_role='admin'` claim no longer grants authorization. Callers
  with grants are narrowed to their authorized subtree. The migration backfills
  `super-admin` and backfills existing enabled members to `compliance-reader` on the
  current root organisations, so existing members keep whole-tree read (a root grant
  covers the tree) and no currently-permitted request is denied.
- Enforce: strict. No legacy fallback; a zero-grant caller sees nothing and cannot
  write. `super-admin` is permitted everywhere in every mode.

The `Authz:Mode` relaxation governs READS only. EVERY mutating route - compliance
writes, user-admin mutations, and role-assignment management - force-enforces via
`RequirePermission(..., alwaysEnforce: true)` (D7), so a `Deny` blocks in every mode,
including `Observe` and `Compat`; no mutating route ever falls through the mode
relaxation. This keeps the model simple (mode = read narrowing only) and never drops a
write below its current admin gate: writes are admin-gated today, and the migration
super-admin backfill plus the admin-create fold-in preserve every current writer, so
force-enforcing writes from day one loses no currently-permitted write. Ship in Compat
with the backfill; flip to Enforce after tests and an operator role review.
Migrations are forward-only; a rollback reverts the web wiring (restoring the old
gates) and a follow-up migration can drop the tables.

### D12: Bootstrapping, org-delete pruning, and last-super-admin prevention

- Bootstrap: `MySqlUserStore.TryBootstrapAdminAsync` inserts the first admin's
  `super-admin` row in `authz_system_role_assignments` in the same transaction as
  the user, credential, and `bootstrap_marker`, so a fresh install is always
  administrable. Existing installs get super-admins from the migration backfill.
- Admin create path: the same principle applies to admins created AFTER bootstrap,
  and the atomic unit MUST be wider than the user row alone. Today
  `AuthFlows.CreateUserAsync` creates the user (`MySqlUserStore.CreateAsync` inserts
  only the users row), then SEPARATELY writes the credential / invite / force-reset
  state; a bearer request rejects a user that has no credential
  (`BearerAuthenticationHandler`). So folding only the `super-admin` assignment into
  the user-row transaction could still produce a COUNTED super-admin who cannot
  authenticate, which would then let the last usable admin be revoked or disabled.
  Requirement: for an admin-created user, the user row, its authentication credential
  (and any force-reset state), AND the `super-admin` system assignment all commit in
  ONE transaction; if the credential write fails the whole create rolls back, leaving
  no orphan `super-admin`. The invite path defers the credential until the invite is
  accepted, so it commits the user, force-reset, and assignment together; the invited
  admin holds no credential yet and is therefore not a usable super-admin (below)
  until acceptance - correct, because it must not be counted as the last admin. This
  is the create analogue of the bootstrap fold-in: any code path that mints a
  `global_role='admin'` user writes the matching `super-admin` assignment atomically
  with the credential.
- Org delete: `DeleteOrganisationAsync` deletes
  `authz_organisation_role_assignments` for the org inside its transaction before
  deleting the organisation (the org FK is RESTRICT); `MySqlGitOpsImporter` prunes
  assignment rows for absent organisations before
  `DeleteAbsentOrganisationsAsync`. The importer needs no knowledge of role
  semantics, only the prune, mirroring how it already prunes scope rows.
- Last-super-admin and self-lockout, surfaced as problems at the endpoint but
  ENFORCED inside a single locking write-store transaction, never as a separate
  endpoint check-then-write. A check in the endpoint followed by an isolated
  `SetEnabledAsync`/revoke statement is TOCTOU-racy: two concurrent revokes or
  disables can both read a count of two and both proceed, zeroing out the last
  admin. The guard MUST be atomic with the mutation: either `SELECT ... FOR UPDATE`
  over the relevant active assignments/users inside the transaction that then
  writes, or a single conditional `UPDATE`/`DELETE` whose `WHERE` includes the
  count guard so it affects zero rows when the guard would be violated. Definitions,
  stated precisely so the guard is unambiguous:
  - An "active super-admin" (equivalently, a USABLE administrator) is a user that is
    ENABLED, holds the `super-admin` system assignment, AND has an authentication
    credential. A disabled user, or a super-admin with no credential (so it cannot
    authenticate), does NOT count toward the last-admin total; the guard's count and
    its `SELECT ... FOR UPDATE`/`WHERE` therefore join the credential table.
  - The "last org-owner" of an organisation is the last DIRECT `org-owner`
    assignment on that organisation (a grant whose `organisation_id` is that org),
    not an owner effective through an ancestor grant. Effective-through-ancestors
    owners are not counted; only direct grants on the organisation are.
  Guards:
  - Revoking the last active `super-admin` is rejected.
  - Disabling a user who is the last active `super-admin` is rejected - closing the
    API self-disable gap in `POST /users/{id}/disable`, not only the admin page.
  - Revoking the last direct `org-owner` of an organisation, or one's own last
    direct `org-owner` grant, is rejected.

## Provenance

Two independent plans converged on the same MIT layering and deny-by-default
engine; they diverged on storage, read timing, org-FK semantics, rollout, audit,
and role naming. The orchestrator/mediator resolved each divergence. Decisions are
tagged by origin.

- D1 (two-layer seam): Plan A and Plan B agreed. Unified.
- D2 (roles/permissions as data): Plan B. Overrides Plan A's code-constant
  role-to-permission map. Binding mediator decision #1.
- D3 (hybrid policy pipeline, deny-overrides): Plan A and Plan B agreed. Unified.
- D4 (subtree by inclusive-ancestor match): Plan A and Plan B agreed. Unified.
- D5 (role set and permission-to-endpoint map): role names `super-admin`,
  `org-owner`, `compliance-manager`, `compliance-reader` and the eight permission
  keys are Plan B (binding mediator decision #6); the keys were checked against
  the actual endpoints in this repo and mapped accordingly.
- D6 (principal/resource from existing claims): Plan A and Plan B agreed;
  limited-session hard-deny input is Plan B / mediator convergence.
- D7 (endpoint filter, page guard, 403 vs 404, fail closed): Plan A and Plan B
  agreed; `AuthzPageGuard` replaces `AdminGuard` per convergence.
- D8 (six data-driven tables, migration 010, store pair): table set is Plan B
  (mediator decision #1); migration number 010 confirmed against the Migrations
  directory (latest is 009); RESTRICT org FK is the orchestrator coupling
  (decision #3) overriding Plan A's single `role_assignments` table with CASCADE.
- D9 (minimal audit table plus ILogger): orchestrator decision #5. Plan A proposed
  logging-only (no table); Plan B proposed audited fallbacks; the mediator required
  a minimal persistent table in addition to logging.
- D10 (read narrowing now): Plan B / concern H1, binding mediator decision #2.
  Overrides Plan A's deferral of read narrowing to a later "Phase B" and its
  permanent transitional `AuthenticatedReadPolicy`. The false-boundary objection
  is decisive: reads narrow this increment.
- D11 (named rollout mode, default-deny in all modes): orchestrator decision #4.
  Replaces Plan A's permanent default-allow read bridge with a mode-gated, audited
  Compat fallback that is removed at Enforce.
- D12 (bootstrap, org-delete prune, last-super-admin, self-disable): Plan B and
  the orchestrator. The prune-before-delete for the RESTRICT org FK is decision #3;
  folding the super-admin assignment into the bootstrap transaction and closing the
  API self-disable gap are Plan B / mediator convergence.

### Divergences and resolutions

1. Storage of roles/permissions. Plan A: code constants and a single
   `role_assignments` table. Plan B: data-driven `authz_*` tables. Resolved to
   Plan B (D2, D8) - mediator decision #1 - so custom roles and an EE designer need
   no schema rewrite.
2. Read narrowing timing. Plan A: deferred (reads stay open via a permanent
   transitional policy). Plan B: now. Resolved to now (D10) - mediator decision #2
   - because per-org authz over unfiltered reads is a false boundary.
3. Organisation FK semantics. Plan A: `ON DELETE CASCADE` (to avoid wedging GitOps
   deletes). Plan B / orchestrator: `RESTRICT` with prune-before-delete. Resolved
   to RESTRICT (D8, D12) - decision #3 - matching the existing convention; the
   wedging concern is solved by the prune the importer already applies to scopes.
4. Rollout. Plan A: default-deny engine with a permanent default-allow read bridge.
   Orchestrator: a named `Observe | Compat | Enforce` mode. Resolved to the mode
   (D11) - decision #4 - so "open reads" is temporary and audited, never permanent.
5. Audit. Plan A: logging only. Orchestrator: minimal persistent table plus logs.
   Resolved to both (D9) - decision #5.
6. Role naming. Plan A: `org_owner`, `org_admin`, `org_member`. Plan B:
   `super-admin`, `org-owner`, `compliance-manager`, `compliance-reader`. Resolved
   to Plan B (D5) - decision #6 - with keys matched to real endpoints.

## Risks / Trade-offs

- A developer adds a mutating endpoint without a permission check -> it inherits no
  gate and leaks. Mitigation: default-deny engine plus an architecture/integration
  test that fails if a mutating compliance/authz route lacks a permission
  requirement (D7).
- Compat fallback is forgotten and Enforce is never flipped -> reads never fully
  narrow for zero-grant callers. Mitigation: the fallback is mode-gated, audited on
  every use, and the flip is a config change gated on a role review (D11).
- Shipping Compat still returns full data to a zero-grant caller. Accepted for the
  Compat window as the audited legacy bridge; the audit trail makes the exposure
  visible and Enforce closes it.
- Per-request fact/ancestry load adds queries. Mitigation: two bounded fact queries
  memoized per request, org grants indexed by `user_id`, ancestry resolved in
  memory; no per-resource query (D8).
- RESTRICT org FK wedges an org delete when an assignment exists. Mitigation:
  prune-before-delete in both the write store and the importer (D12), the pattern
  already proven for scopes.
- The engine says permit but an endpoint forgets to ask, or vice versa.
  Mitigation: web/E2E tests with two users on different org subtrees proving SoA
  and selector isolation, plus a super-admin bypass test.
- Reason strings leak internal detail -> reasons are audit-only, never in the
  problem body (D7).
- The reparent write locks only the single parent row it rechecks, not the whole
  ancestry chain. A caller whose `org.write` permit derived from an ANCESTOR grant
  could, in a narrow window, have that ancestor concurrently reparented out of
  their subtree while their own reparent commits. Accepted as negligible-risk: no
  privilege escalation - the actor already administers both the source and
  destination subtrees, so this is a lapsed-authority race among trusted org
  admins, not an outsider gaining access. Ancestry-chain locking is disproportionate
  for a foundational component; noted as possible future hardening.

## Migration Plan

1. Land the Core engine, policy pipeline, fact-provider port, and action catalog
   (pure, unit-tested).
2. Land migration 010 (tables, seed, super-admin backfill) and the read/write
   store pair, plus the org-delete prune and the importer prune (MySQL-gated
   tests).
3. Land the web authorizer, principal factory, endpoint filter, page guard,
   `AuthzOrgAccess`, and the rollout mode; wire DI (`AddAuthz`, replace
   `AllOrgAccess`); add fakes and web tests. Default mode Compat.
4. Apply read narrowing to the list endpoints and the SoA endpoint/page; move
   compliance writes and user admin to permission checks; add the org-owner grant
   on org create.
5. Add the role-assignment management endpoints and admin page with the
   last-super-admin, self-disable, and last-owner guards; fold the super-admin
   assignment into the bootstrap transaction.
6. Add architecture tests (MIT placement, Agent/CLI authz-free, every mutating
   route gated) and E2E coverage.
7. Verify: `dotnet build`, `dotnet test` (unit/web green without MySQL), then the
   MySQL-gated and E2E-gated suites. Flip `Authz:Mode` to Enforce after an operator
   role review.

## Open Questions and Unresolved Tensions

- Compat exposure window: a zero-grant caller keeps full READ under Compat (writes
  never fall back - see D11). Reads narrow for callers with grants; only zero-grant
  callers get the read fallback. Is the audited read fallback acceptable until the
  Enforce flip? (Assumed: yes, the audit trail makes it visible and Enforce closes
  it.)
- Member read backfill (now decided, see D11 and the persistence tasks): existing
  enabled non-admin members are backfilled to `compliance-reader` on the current
  root organisations so they keep whole-tree read the moment Compat ships. Left here
  only for reviewers to confirm the scope (enabled non-admins, root organisations).
- `compliance-manager` vs `org-owner` split for `authz.assignment.write`: only
  `org-owner` can manage assignments. Confirm a compliance-manager should not
  delegate roles.
- Catalog reads left global: standards/controls/requirements stay authenticated-
  only. Reviewers should confirm no tenant-specific catalog data is planned that
  would make this a boundary.
- Whether the SoA JSON endpoint filtering should 404 an unauthorized standard-level
  request or return an empty node set (assumed: return the accessible subset,
  consistent with the list endpoints).
