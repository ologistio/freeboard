## 1. Core authorization engine and catalog (feat(core): authorization engine)

- [x] 1.1 Add `Freeboard.Core/Authz/` request and decision types: `AuthzEffect`
  (`Deny`, `Permit`), `AuthzDecision(Effect, Reason)`,
  `AuthzResource(Type, Id?, OrganisationId?, OrgAncestryInclusive)`,
  `AuthzPrincipal` (user id, is-authenticated, is-limited-session, is-stepped-up,
  effective system permissions, org grants as `(PermissionKey, OrganisationId)`),
  and `AuthzRequest(Principal, Action, Resource)`.
- [x] 1.2 Add the `AuthzActions` constant catalog: `system.admin`,
  `authz.assignment.write`, `org.read`, `org.write`, `compliance.read`,
  `compliance.scope.write`, `compliance.requirement-scope.write`, `user.manage`.
  Add the `IAuthzFactProvider` port (load a principal's effective permissions and
  org grants) with no persistence dependency.
- [x] 1.3 Add `IAuthzPolicy` (returns `Permit`/`Deny`/`NotApplicable`), the ordered
  policies (`SessionGuardPolicy`, `SystemAdminPolicy`, `SelfAccessPolicy` inert
  slot, `OrgRbacPolicy`), and `IAuthorizationEngine` + `PolicyAuthorizationEngine`
  with deny-overrides, default-deny combining.
- [x] 1.4 Unit-test the engine in `Freeboard.Core.Tests`: default-deny,
  deny-overrides, unauthenticated/limited hard-deny, `system.admin` permit-all,
  RBAC inclusive-ancestor match (grant on ancestor permits descendant; sibling
  denied; cyclic ancestry terminates), decision-against-supplied-facts (no
  hard-coded role map), and the inert self-access slot.
- [x] 1.5 Verify: `dotnet build src/Freeboard.Core` and
  `dotnet test tests/Freeboard.Core.Tests`.

## 2. Persistence: migration, seed, and authz stores (feat(persistence): authorization tables)

- [x] 2.1 Add `src/Freeboard.Persistence/Migrations/010_authorization.sql` creating
  the six `authz_*` tables per design D8 (`utf8mb4_bin` ids; FK cascade on
  user/role subject, RESTRICT on organisation and permission; audit table with
  scalar ids and no strict FKs). `authz_roles` carries a `scope` column with values
  `system` or `organisation`, a persisted invariant that constrains which assignment
  table a role may be written to. Seed the four roles (scope `super-admin`=`system`,
  `org-owner`/`compliance-manager`/`compliance-reader`=`organisation`), eight
  permissions, and role-to-permission rows (`is_system=1`); backfill `super-admin`
  system assignments from `users.global_role='admin'`. Make the migration idempotent/
  re-runnable (the runner replays a partially-failed migration): `CREATE TABLE IF
  NOT EXISTS` for all six tables and `INSERT ... ON DUPLICATE KEY UPDATE` (or
  `INSERT IGNORE`) for every seed and backfill row.
- [x] 2.1a Member backfill in migration 010: backfill every existing ENABLED
  non-admin user to `compliance-reader` on the current ROOT organisations (a root
  grant covers the whole tree), so the Enforce flip hides nothing from existing
  members and no currently-permitted read is denied. Use the same idempotent
  `INSERT ... ON DUPLICATE KEY UPDATE`/`INSERT IGNORE` form so a re-run does not
  duplicate or error.
- [x] 2.2 Add read models and `IAuthzStore` (`LoadPrincipalFactsAsync` in bounded
  queries, `ListSystemAssignmentsAsync`, `ListOrganisationAssignmentsAsync`) with
  `MySqlAuthzStore` (Dapper, `col AS Prop`, `RepeatableRead` for multi-table
  decisions). Fact loading SHALL defensively ignore a mis-scoped assignment row
  (a `system`-scoped role in an organisation assignment, or an `organisation`-scoped
  role in the system assignment table) by joining on `authz_roles.scope`, so a stray
  row from a schema breach can never contribute `system.admin` (permit-all) to a
  principal's facts.
- [x] 2.3 Add `IAuthzAdministrationStore` (`AssignSystemRoleAsync`,
  `RevokeSystemRoleAsync`, `AssignOrganisationRoleAsync`,
  `RevokeOrganisationRoleAsync`, `AppendAuditEventAsync` returning `WriteResult`)
  with `MySqlAuthzAdministrationStore`: validate known role and existing user/org;
  validate role scope against the assignment table - reject a `system`-scoped role
  through the organisation-role write and an `organisation`-scoped role through the
  system-role write, returning the validation-failure result (422/400), so a
  mis-scoped grant can never be persisted; map duplicate to a conflict. Enforce the
  last-super-admin and last-owner guards
  INSIDE the single locking write-store transaction that performs the revoke, never
  as a separate check-then-write: `SELECT ... FOR UPDATE` over the active
  assignments (or a conditional `DELETE` whose `WHERE` carries the count guard) so
  two concurrent revokes cannot both pass. An "active"/usable super-admin (per D12) is
  a user that is ENABLED, holds `super-admin`, AND has an authentication credential; a
  disabled user or a credential-less super-admin does NOT count, so the revoke path's
  `SELECT ... FOR UPDATE` / `WHERE` count guard JOINS the credential table. "Last
  org-owner" = the last DIRECT `org-owner` assignment on that organisation (not
  effective-through-ancestors).
- [x] 2.4 Prune-before-delete for the RESTRICT org FK: in
  `MySqlComplianceWriteStore.DeleteOrganisationAsync` delete the org's
  `authz_organisation_role_assignments` inside the transaction before deleting the
  organisation; in `MySqlGitOpsImporter` prune absent-org assignment rows before
  `DeleteAbsentOrganisationsAsync`.
- [x] 2.5 Fold the first-admin `super-admin` assignment into
  `MySqlUserStore.TryBootstrapAdminAsync` (same transaction as user, credential,
  and bootstrap marker).
- [x] 2.5a Widen the admin-create path to ONE transaction: when a user is created
  with `global_role='admin'` through the API (`AuthFlows.CreateUserAsync` ->
  `MySqlUserStore`), the user row, its authentication credential, any force-reset
  state, AND the matching `super-admin` system assignment SHALL all commit in the
  SAME transaction. Today `AuthFlows.CreateUserAsync` creates the user, then
  SEPARATELY writes the credential/force-reset (and `MySqlUserStore.CreateAsync`
  inserts only the users row), so a super-admin could be counted while unable to
  authenticate. If the credential write fails the whole create SHALL roll back,
  leaving no orphan `super-admin`. The invite path (no credential until the invite is
  accepted) commits the user, force-reset, and `super-admin` assignment together; the
  invited admin holds no credential yet and so is not a usable super-admin (redefined
  in D12) until acceptance, which is correct - it is not counted as the last admin.
  This closes the lockout hole now that `global_role='admin'` no longer grants
  authorization (D11, D12). Cross-reference the bootstrap fold-in: every path that
  mints an admin user writes the assignment atomically with the credential.
- [x] 2.6 Add `AddAuthz(connectionString)` to `PersistenceServiceCollectionExtensions`
  registering both stores as singletons through the shared connection factory.
- [x] 2.7 Add MySQL-gated integration tests in `Freeboard.Persistence.Tests`:
  migration applies and seeds (roles carry the expected `scope`); admin backfill;
  member backfill (enabled non-admin gets `compliance-reader` on the root
  organisations); migration re-run is idempotent (apply the file twice, no duplicate
  or error); admin-create path writes the user, credential, and `super-admin`
  assignment atomically; a failed credential write during admin create rolls back the
  whole create and leaves NO `super-admin` assignment (no orphan counted admin);
  user-delete cascade; org-delete prune (app path and importer); assign/revoke
  round-trip; duplicate rejected; a mis-scoped grant is rejected (assigning
  `super-admin` through the organisation-role write, or an organisation-scoped role
  through the system-role write, returns the validation-failure result and writes
  nothing); a mis-scoped assignment row is ignored by the fact loader (a stray row
  never grants permit-all); last-super-admin and last-owner rejected; a credential-less
  `super-admin` assignment does NOT let the sole usable super-admin be revoked or
  disabled (the credential-less row is not counted as a survivor); concurrency:
  two parallel revokes of the last usable `super-admin` cannot both succeed, two
  parallel DISABLES of the last two usable super-admins cannot both succeed, and a
  revoke-vs-disable race on the last two usable super-admins cannot zero them out; a
  persisted mutation writes an `authz_audit_events` row; effective-permission fact
  load.
- [x] 2.8 Verify: `dotnet build src/Freeboard.Persistence`;
  `dotnet test tests/Freeboard.Persistence.Tests` (skips cleanly without
  `FREEBOARD_TEST_DB`, run once with it set).

## 3. Web authorizer, enforcement, and rollout mode (feat(web): authorizer and enforcement)

- [x] 3.1 Add `src/Freeboard/Authz/`: `ClaimsPrincipalAuthzPrincipalFactory`,
  `Authorizer` (`IAuthorizer.AuthorizeAsync` loading facts via `IAuthzStore`
  once per request, resolving inclusive org ancestry via the shared parent-walk
  helper (3.1a), calling the engine, applying `Authz:Mode`, failing closed). Audit:
  `ILogger` always logs the decision; the `authz_audit_events` write is best-effort
  and is skipped-and-logged on failure, including when the failed dependency is the
  authz store itself. The persisted `authz_audit_events` set is exactly the
  security-relevant trail: every denied decision, AND every zero-grant Compat READ
  through the legacy read fallback (it is the exposure the Enforce flip closes, so the
  trail must record who used the bridge). An ordinary permit is ILogger-only, not
  persisted. In `Observe` mode audit the would-be decision without blocking.
- [x] 3.1a Extract the inclusive-ancestry build shared by both callers into ONE
  helper (a pure function over the org list) with the visited-set cycle guard, and
  call it from BOTH the SoA projection and the `Authorizer`, so they cannot diverge on
  the cycle-guard behaviour that RBAC correctness depends on (D4). Note the two
  callers consume the chain differently: `StatementOfApplicability` currently
  short-circuits its parent-walk at the NEAREST ancestor that has a disposition,
  whereas the authorizer needs the FULL inclusive ancestry `[R, parent(R), ..., root]`
  to match any granting ancestor. The shared helper builds the full chain; the
  authorizer uses it whole, and the SoA projection consumes the same chain but stops
  at the first node with a disposition. This is an extraction of shared build logic,
  not a reuse of one existing private helper.
- [x] 3.2 Add `AuthzEndpointExtensions.RequirePermission(action, resourceSelector,
  alwaysEnforce = false)` and `AuthzEndpointFilter`: run the selector for the
  `AuthzResource`, call the authorizer, short-circuit denied calls (403 for a visible
  resource, 404 for an invisible org-scoped resource); treat a missing/throwing
  selector as deny. When `alwaysEnforce` is true the filter blocks a `Deny` in EVERY
  mode, bypassing the `Authz:Mode` relaxation that governs reads only (the authorizer
  exposes the underlying always-block decision path, e.g. a mode-independent
  `AuthorizeAsync` overload or flag). EVERY mutating route sets `alwaysEnforce: true`
  (compliance writes, user-admin mutations, and role-management), so no write is
  mode-relaxed in any mode. Add `AuthzPageGuard` replacing `AdminGuard`; the page
  guard likewise always blocks.
- [x] 3.3 Add `AuthzOrgAccess` implementing the (now async) `IOrgAccess`, resolving
  the accessible set by mode. Under `Observe` it returns the FULL accessible set for
  every caller regardless of grants (reads are not narrowed, so read behaviour is
  unchanged while decisions are observed - D11). Under `Compat` it narrows a
  grant-holder to its authorized subtree union and gives a zero-grant caller the
  audited full read fallback. Under `Enforce` it is strict: the subtree union, and a
  zero-grant caller sees nothing. A super-admin gets all organisations in every mode.
  Update `OrgSelectionResolver` and the SoA page call sites for the async seam.
- [x] 3.4 Register in `Program.cs`: `AddAuthz` stores, the Core engine + policies,
  `IAuthorizer`, the `IAuthzFactProvider` binding, and `Authz:Mode`; replace the
  `AllOrgAccess` registration with `AuthzOrgAccess`. No global fallback policy.
  Lifetime change: register `AuthzOrgAccess`, the `Authorizer`, and the shared
  request-scoped fact/grant cache as SCOPED (not the singleton the current
  `AllOrgAccess` uses), sharing ONE cache so per-user grants load once per request
  and never leak across requests (D1, D11).
- [x] 3.5 Add a `FakeAuthzStore` in `Freeboard.Web.Tests` and web tests via
  `ConfigureTestServices`: permitted caller passes, unpermitted 403, invisible
  resource 404, super-admin bypass, fail-closed on store outage, limited-session
  denial, Observe vs Compat vs Enforce behavior, reads are not narrowed under Observe
  (a caller with a partial-subtree grant gets the FULL accessible set under Observe,
  D11), a denied decision writes an `authz_audit_events` row, and grants do not leak
  across two requests (scoped cache).
- [x] 3.6 Verify: `dotnet build`; `dotnet test tests/Freeboard.Web.Tests`.

## 4. Narrow reads and permission-gate writes (feat(compliance): scoped reads and writes)

- [x] 4.1 Filter `GET /organisations`, `/scopes`, `/requirement-scopes` to the
  accessible set, and filter the SoA JSON endpoint nodes to the accessible subtree
  (resolve over the full tree first). Leave catalog reads global. On
  `GET /organisations`, when a returned organisation's parent is NOT in the caller's
  accessible set, null its `parent` id in the response (non-disclosure) - the parent
  id would otherwise leak an inaccessible organisation's existence; the selector
  already treats such a node as a root.
- [x] 4.2 Replace `RequireAuthorization(GlobalRoles.AdminPolicy)` on the compliance
  write group with `RequirePermission(..., alwaysEnforce: true)` per D5 (`org.write`,
  `compliance.scope.write`, `compliance.requirement-scope.write`), using selectors
  that read the target organisation from the route/body (delete resolves the org
  from the stored row). `alwaysEnforce: true` because writes force-enforce in every
  mode - the admin gate they replace blocks today in every mode, so Observe/Compat
  must not open them (D7, D11). Because a PUT upsert can MOVE a row across organisations
  (`ON DUPLICATE KEY UPDATE` on `organisation_id`/`parent_id`), a PUT on an existing
  scope or requirement-scope MUST authorize BOTH the stored row's current owning org
  AND the requested new org. Organisation PUT: creating a root (no parent) requires
  `system.admin`; creating a child authorizes `org.write` on the parent;
  reparenting authorizes `org.write` on BOTH the current and the new parent. The
  selector reads the stored row before the write to obtain the current owning org.
- [x] 4.3 On successful organisation create through the write API, grant the creator
  `org-owner` on the new org unless the creator is a super-admin.
- [x] 4.4 Move `UserAdminEndpoints` to
  `RequirePermission(user.manage, ..., alwaysEnforce: true)` (super-admin-only,
  behavior unchanged; `alwaysEnforce: true` so the mutations force-enforce in every
  mode, matching the admin gate they replace - D7, D11) and add the last-super-admin
  disable guard
  to `DisableUserAsync`, enforced atomically in the user-store write (conditional
  `UPDATE ... WHERE` count guard or `SELECT ... FOR UPDATE` in the same transaction),
  not as an endpoint check-then-write. The count guard counts USABLE super-admins per
  D12 - a user that is enabled, holds the `super-admin` assignment, AND has an
  authentication credential - so a super-admin who cannot authenticate is never
  treated as the surviving last admin (its `WHERE`/`FOR UPDATE` joins the credential
  table). Append an `authz_audit_events` row (best-effort, per 3.1) at the user-admin
  mutation sites (create, disable, enable, reset) so the persisted set matches the
  spec's authz/user/admin mutation trail. After BOTH this migration and the compliance
  write migration (4.2) land, remove the now-dead `GlobalRoles.AdminPolicy` policy
  registration in `Program.cs` (`AddPolicy(AdminPolicy, RequireClaim(role, admin))`)
  and the orphaned `GlobalRoles.AdminPolicy` constant: after 4.2 and 4.4 its only two
  consumers (`ComplianceWriteEndpoints`, `UserAdminEndpoints`) no longer reference it.
  Verify no other consumer remains before removing (the `GlobalRoles.Admin` claim
  value stays - it is still set and read elsewhere; only the `AdminPolicy` name is
  dead).
- [x] 4.4a Gate the cross-user branch of the `AuthEndpoints` session routes
  (`GET`/`DELETE /auth/sessions/{id}`, `GET`/`DELETE /users/{id}/sessions`) on
  authorization instead of the legacy admin claim (D5, D7). Replace the `IsAdmin`
  claim derivation that feeds `callerIsAdmin` into `AuthFlows` with an `IAuthorizer`
  `user.manage` decision on the target `user` resource, force-enforced in every mode
  (a whole-route `RequirePermission` filter cannot be used because these routes also
  serve self-service; use an in-handler authorizer call like `AuthzPageGuard`). The
  self path (owner-id match in `AuthFlows.CanActOn`) is unchanged and needs no
  permission; the self-service page handlers (`Pages/Account/Sessions`,
  `SessionsRevoke`) already pass `callerIsAdmin: false` and are unaffected. Audit the
  cross-user decision (best-effort, per 3.1). Mark each of the four routes with
  cross-user permission metadata (`user.manage`) so the route-metadata test (6.2)
  covers them. The legacy `freeboard:role=admin` claim SHALL grant nothing on these
  routes. Once the four routes no longer feed `callerIsAdmin` from the claim, the
  private `IsAdmin(ClaimsPrincipal)` helper in `AuthEndpoints.cs` has no remaining
  caller; verify no other caller remains and DELETE it, so no dead code is left.
- [x] 4.4b Web tests for the session routes: a caller holding only the legacy admin
  claim (no `user.manage`/`system.admin`) is refused listing and revoking ANOTHER
  user's sessions in every mode (self existence non-disclosure preserved); a caller
  still lists and revokes its OWN sessions with no grant; a `system.admin` caller
  lists and revokes another user's sessions and the decision is audited.
- [x] 4.4c Migrate the admin user-management Razor pages off `AdminGuard` (D5, D7):
  `Pages/Admin/Users.cshtml.cs` (OnGet plus the Create/Disable/Enable/ResetPassword
  handlers) and `Pages/Admin/UserCredential.cshtml.cs` (OnGet) call `AdminGuard.Check`
  (which reads the legacy `freeboard:role=admin` claim) and mutate by calling
  stores/flows DIRECTLY, so neither the `RequirePermission` filter nor the
  route-metadata test reaches them. Replace each `AdminGuard.Check` call with an
  in-handler `AuthzPageGuard` check for `user.manage` on the target `user` resource
  (these are pure admin pages with no self-service branch). DELETE
  `Pages/Admin/AdminGuard.cs` - these two pages are its only callers. The legacy admin
  claim SHALL grant nothing on these pages.
- [x] 4.4d Web tests for the admin user-management pages (the page analogue of 4.4b):
  a caller holding only the legacy `freeboard:role=admin` claim, with no
  `user.manage`/`system.admin` grant, is refused (bare 403) on `/admin/users` (OnGet
  and each mutating handler) and the user-credential page (OnGet) in EVERY rollout
  mode; a `system.admin` caller is admitted.
- [x] 4.4e Make the admin nav-link visibility follow authz, not the legacy claim
  (D5). `Pages/Home.cshtml` and `Pages/Shared/_Layout.cshtml` currently show the
  admin link when `freeboard:role=admin` is set. After this change that claim no
  longer grants admin access, so the link is misaligned: a super-admin granted via
  authz would see no link, and a stale-claim holder would see a dead link (403 on
  click). Show the link when the principal can reach the admin surface - it holds
  `user.manage` or `system.admin` (from the loaded permissions / the authorizer) -
  instead of when it merely has `freeboard:role=admin`. This is a cosmetic UX
  affordance, not a security control: the admin pages force-enforce regardless
  (4.4c), so keep it fail-safe - hide the link on any error resolving access.
- [x] 4.5 Update web tests: narrowed reads for a scoped caller; a returned org whose
  parent is inaccessible has a null `parent` id; super-admin sees all; global admin
  still writes; a non-admin `org-owner` writes within its subtree; a
  `compliance-reader` is denied a write; a non-admin zero-grant caller is denied a
  compliance write under Compat (no admin-claim write fallback); a denied compliance
  write is blocked while `Authz:Mode=Observe` and a denied user-admin mutation is
  blocked while `Authz:Mode=Observe` (writes force-enforce in every mode, not only
  role-management, 3.2/4.2/4.4); a cross-org move
  (PUT that would change a row's owning org) is denied when the caller lacks write
  on the stored org; an org reparent requires write on both parents; creator becomes
  owner; self-disable of the last usable super-admin rejected; disabling a super-admin
  is allowed while another usable super-admin remains but rejected when it would leave
  none usable.
- [x] 4.6 Verify: `dotnet build`; `dotnet test tests/Freeboard.Web.Tests`.

## 5. Role-assignment management API and admin page (feat(web): role management)

- [x] 5.1 Add `/api/v1/freeboard/` role-assignment endpoints with concrete
  contracts, consistent with the existing endpoint style (JSON bodies, RFC 7807
  problems):
  - `GET /api/v1/freeboard/organisations/{orgId}/role-assignments` - list org-scoped
    assignments on that org. 200 with a JSON array of
    `{ user_id, role_key, organisation_id, created_at }`.
  - `PUT /api/v1/freeboard/organisations/{orgId}/role-assignments` - grant, body
    `{ user_id, role_key }`. 201 on create, 409 on an existing grant.
  - `DELETE /api/v1/freeboard/organisations/{orgId}/role-assignments/{userId}/{roleKey}`
    - revoke. 204 on success, 404 when the grant does not exist, 409 (problem) when
    the revoke would remove the last direct `org-owner`.
  - `GET /api/v1/freeboard/system-role-assignments` - list `super-admin` holders.
    200 with `{ user_id, role_key, created_at }`.
  - `PUT /api/v1/freeboard/system-role-assignments` - grant `super-admin`, body
    `{ user_id }`. 201 on create, 409 on an existing grant.
  - `DELETE /api/v1/freeboard/system-role-assignments/{userId}` - revoke. 204 on
    success, 404 when not held, 409 (problem) when it would remove the last active
    `super-admin`.
  Org-scoped endpoints sit behind
  `RequirePermission(authz.assignment.write, org, alwaysEnforce: true)`; system
  endpoints behind `RequirePermission(system.admin, ..., alwaysEnforce: true)`.
  403-vs-404: a caller who cannot see the organisation at all gets 404 (existence
  non-disclosure); a caller who can see it but lacks the assignment permission gets
  403. `alwaysEnforce` makes them block a `Deny` in every mode (including `Observe`
  and `Compat`), and they enforce the last-super-admin and last-owner guards
  atomically in the write store (2.3).
- [x] 5.2 Add an admin Razor Page (SSR, under `/Admin`) to view an organisation's
  assignments and grant/revoke roles; the handler uses `AuthzPageGuard` (pipeline
  policies do not run for page handlers).
- [x] 5.3 Add web tests: grant/revoke, denied without `authz.assignment.write`,
  system role denied without `system.admin`, last-super-admin protected, a revoke of
  the last direct `org-owner` of an organisation returns 409 (the last-owner contract,
  through the role-management API), a denied role-management mutation is blocked while
  `Authz:Mode=Observe` (management endpoints force-enforce regardless of mode, 3.2),
  page render/permission test.
- [x] 5.4 Verify: `dotnet build`; `dotnet test tests/Freeboard.Web.Tests`.

## 6. Architecture and end-to-end verification (test: authz placement and E2E)

- [x] 6.1 Extend `Freeboard.Architecture.Tests`: the engine and catalog are MIT
  (Core); no authz type references `Freeboard.Enterprise`; and `Freeboard.Agent` and
  `Freeboard.CLI` reference no `Freeboard.Enterprise` assembly and register/wire no
  web authz enforcement (no type from the `Freeboard/Authz` namespace), because
  enforcement is web-only. Do NOT assert the Agent/CLI build output carries no authz
  store type: `Freeboard.CLI` references `Freeboard.Persistence`, so the MIT Core
  authz engine and the Persistence authz stores are present transitively in its
  output by design - that is expected and is not a violation. If an architecture test
  can meaningfully assert it, assert "no `Freeboard.Enterprise` reference" and "no
  web authz-enforcement type wired in Agent/CLI"; otherwise keep the two genuine
  guarantees (no EE reference; Agent/CLI gain no authz enforcement).
- [x] 6.2 Add an integration/architecture assertion over route metadata that every
  mutating compliance, authz, and user-admin route carries BOTH a permission
  requirement AND `alwaysEnforce: true` in its endpoint filter metadata. The
  `alwaysEnforce` half is load-bearing: because the `Authz:Mode` relaxation governs
  reads only and writes must force-enforce (D7, D11), a mutating route wired with
  `alwaysEnforce: false` would be silently mode-relaxed in Observe/Compat and its
  `Deny` would not block - so the metadata test asserts both, not just presence of a
  permission. This metadata test - not runtime "fail closed" - is the real guard
  against a forgotten or mis-wired FILTER, because a filter-gated route with no filter
  still runs its handler; assert on the metadata, and include the user-admin mutating
  routes, not only compliance and role-assignment. The metadata test covers
  filter-gated minimal-API routes ONLY. Also assert each cross-user `AuthEndpoints`
  session route (`GET`/`DELETE /auth/sessions/{id}`, `GET`/`DELETE /users/{id}/sessions`)
  carries its cross-user permission metadata (`user.manage`, force-enforced) from
  4.4a; these routes gate the cross-user branch in-handler (they also serve
  self-service, so they cannot carry a whole-route filter). The metadata assertion on
  the session routes only proves the annotation is PRESENT; it CANNOT verify the
  in-handler authorizer call still runs, so a dropped in-handler check is caught by the
  behavioral web tests (4.4b), NOT by this metadata test. The `AuthzPageGuard` on the
  admin user-management pages is likewise NOT covered by this metadata test; its
  behavioral page tests (4.4d) guarantee it.
- [x] 6.3 Add E2E coverage in `Freeboard.WebE2E` (gated): two users assigned to
  different org subtrees prove SoA and selector isolation; an org owner performs a
  scoped write and manages a role; a reader is denied a write. SSR flows only.
- [x] 6.4 Final verification: `dotnet build`; `dotnet test` (unit/web green without
  MySQL); then run the `FREEBOARD_TEST_DB`-gated and `FREEBOARD_TEST_E2E`-gated
  suites. Flip `Authz:Mode` to Enforce after an operator role review.
