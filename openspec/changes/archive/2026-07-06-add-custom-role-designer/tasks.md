## 1. Core: custom-role authoring rules (MIT)

- [x] 1.1 Add `Freeboard.Core/Authz/AuthzCustomRoles.cs`: `public const string
  CustomRoleKeyPrefix = "custom:";`, `AuthorablePermissionKeys` (the five org-tree
  keys via `AuthzActions` constants: `OrgRead`, `OrgWrite`, `ComplianceRead`,
  `ComplianceScopeWrite`, `ComplianceRequirementScopeWrite`), and
  `IsAuthorableRoleKey(string roleKey)` (prefix present, remainder a bounded
  lowercase ASCII hyphenated slug, total length <= 64). Short doc comments only.
- [x] 1.2 Add a `Freeboard.Core.Tests` unit test asserting: the authorable set is
  exactly the five keys and contains none of `system.admin`, `user.manage`,
  `authz.assignment.write`; `IsAuthorableRoleKey` accepts a valid `custom:` key
  and rejects an unprefixed key, an over-length key, and a bad-slug key.
- [x] 1.3 (commit `feat(core): add custom-role authoring rules`)

## 2. Persistence: custom-role read/write on the authz stores (MIT)

- [x] 2.1 Add read-model records to `Freeboard.Persistence/AuthzReadModels.cs`:
  `CustomRoleRow(RoleKey, Title, Description, Scope, IsSystem, CreatedAt,
  UpdatedAt)` and `RoleWithPermissions(CustomRoleRow Role,
  IReadOnlyList<string> PermissionKeys)`.
- [x] 2.2 Extend `IAuthzStore` with `ListCustomRolesAsync(ct)` (returns
  `is_system = 0` roles) and `GetRoleAsync(roleKey, ct)` (returns
  `RoleWithPermissions?`); implement both in `MySqlAuthzStore`.
- [x] 2.3 Extend `IAuthzAdministrationStore` with `CreateCustomRoleAsync`,
  `UpdateCustomRoleAsync`, and `DeleteCustomRoleAsync`, each taking the actor
  user id and returning `AuthzWriteResult`.
- [x] 2.4 Implement the three writes in `MySqlAuthzAdministrationStore`. The create
  signature takes no `scope`: write `scope = 'organisation'` and `is_system = 0` as
  constants (the store can never mint a non-organisation or system custom role, so
  there is no scope-rejection path at create). Require
  `AuthzCustomRoles.IsAuthorableRoleKey` on create; validate `title` non-blank and
  `<= 190` chars, and `description` (optional: null/omitted is stored as an empty
  string) `<= 512` chars, returning `Invalid` and writing nothing on a blank or
  over-length title or an over-length description; reject any permission key not
  in `AuthzCustomRoles.AuthorablePermissionKeys` (this subsumes the excluded
  `system.admin`/`user.manage`/`authz.assignment.write` keys); insert the role row
  and its `authz_role_permissions` rows in one transaction; map duplicate
  `role_key` to `Conflict`. Update changes title/description/permissions only,
  never `role_key`/`scope`/`is_system`. Update and delete reject `is_system = 1`
  targets. Delete locks the role row, counts assignments in
  `authz_organisation_role_assignments` and `authz_system_role_assignments` inside
  the transaction (non-zero -> `Conflict`), and maps an FK-restrict failure to
  `Conflict`; an unused delete cascades permission rows.
- [x] 2.5 In each of the three writes, insert the `authz_audit_events` row inside
  the SAME transaction (extract a private transaction-aware audit-insert helper
  from the existing `AppendAuditEventAsync` body): event types
  `authz.role.create`/`authz.role.update`/`authz.role.delete`,
  `resource_type = "authz_role"`, `resource_id = role_key`, `effect = "Permit"`,
  `actor_user_id` from the passed actor. Leave `AppendAuditEventAsync` and the
  existing assignment-route audit path unchanged.
- [x] 2.6 Add MySQL integration tests in
  `tests/Freeboard.Persistence.Tests/AuthzIntegrationTests.cs` (gated on
  `FREEBOARD_TEST_DB`): create writes `scope`/`is_system`/permissions and an audit
  row atomically; create/read round-trip; created role is always
  `scope = 'organisation'` and `is_system = 0`; duplicate-key conflict;
  non-authorable key (including an excluded key) rejected and writes nothing;
  blank title, over-length title, and over-length description each rejected and
  write nothing; unprefixed key rejected; update replaces permissions and writes
  an audit row; a rejected update (non-authorable key or invalid title) leaves the
  role's title, description, and `authz_role_permissions` rows unchanged and writes
  NO audit row; seeded-role update/delete rejected; in-use delete blocked; unused
  delete removes the role, cascades permission rows, and writes an audit row;
  assigning a created custom role expands the assignee's facts.
- [x] 2.7 (commit `feat(persistence): add custom-role CRUD to the authz stores`)

## 3. Enterprise: custom-role presentation catalog (EE)

- [x] 3.1 Add a static presentation catalog to `Freeboard.Enterprise` mapping each
  key in `AuthzCustomRoles.AuthorablePermissionKeys` to a label, description, and
  group for the designer UI; reference `Freeboard.Core` only. The catalog type is
  `public` so the web project and the web test project (which reach Enterprise
  transitively through `Freeboard`) can reference it. The catalog SHALL be a subset
  of the Core allow-list and MUST NOT introduce any key not in it.
- [x] 3.2 Add a unit test (in the web test project, which already references
  Enterprise) asserting the presentation catalog covers exactly
  `AuthzCustomRoles.AuthorablePermissionKeys` and introduces no extra key.
- [x] 3.3 Extend `tests/Freeboard.Architecture.Tests/EnterpriseReferenceTests.cs`
  with an assertion pinning the forward EE rule: parse
  `src/Freeboard.Enterprise/Freeboard.Enterprise.csproj` and assert its only
  `ProjectReference` is `Freeboard.Core` (no web or persistence reference). The
  existing tests only pin the reverse direction (community projects carry no
  Enterprise reference), so this closes the gap the new EE catalog exposes.
- [x] 3.4 (commit `feat(enterprise): add the custom-role presentation catalog`)

## 4. Web: entitlement filter and custom-role API (EE surface)

- [x] 4.1 Add `RequireEntitlement(this RouteHandlerBuilder/RouteGroupBuilder,
  EnterpriseEntitlement)` in `src/Freeboard/Entitlements/` as an endpoint filter
  that resolves `IEnterpriseEntitlements` and returns 404 when not entitled.
- [x] 4.2 Extract `SystemSelector` from `RoleAssignmentEndpoints` into a shared
  internal static helper in the `Freeboard.Authz` namespace and point both
  `RoleAssignmentEndpoints` (system routes) and the new `CustomRoleEndpoints` at it
  (it is identical and security-relevant, so one definition, not two copies). The
  `ValidationProblem` (422) and `Conflict` (409) responders are NOT shared: they
  differ only by problem title, so `CustomRoleEndpoints` keeps its own small local
  copies with custom-role titles.
- [x] 4.3 Add `src/Freeboard/Authz/CustomRoleEndpoints.cs` mapping
  `GET/POST /custom-roles`, `GET/PUT/DELETE /custom-roles/{roleKey}` under
  `ApiRoutes.ApiRoutePrefix`; apply `RequireEntitlement(CustomPolicies)` then
  `RequirePermission(AuthzActions.SystemAdmin, SystemSelector, alwaysEnforce:
  true)` on the group (entitlement filter first, so an unentitled super-admin gets
  404 not 403). Do NOT mark the routes as `AuthEndpoint`, so GitOps read-only mode
  blocks their mutations with 409 (D12). Validate the submitted `permission_keys`
  against the Core allow-list and `title`/`description` against the column widths,
  call the store passing the actor user id, and map `AuthzWriteResult` to status
  codes via the local problem-detail helpers. Audit is written by the store
  (task 2.5), not the endpoint.
- [x] 4.4 Register the endpoints in `Program.cs` (`app.MapCustomRoleEndpoints()`).
- [x] 4.5 Add an entitlement toggle to `tests/Freeboard.Web.Tests/AuthWebFactory.cs`:
  a `bool CustomPoliciesEntitled { get; init; }` init property that sets
  `builder.UseSetting("Enterprise:CustomPolicies", CustomPoliciesEntitled ? "true" :
  "false")`, so a test can boot the host with the entitlement on or off (the factory
  has no such control today, but tasks 4.6 and 5.4 need both states).
- [x] 4.6 Add `InlineData` rows to
  `tests/Freeboard.Web.Tests/RouteAuthzMetadataTests.cs` for POST/PUT/DELETE
  `custom-roles` with `AuthzActions.SystemAdmin`; the universal guard already
  covers new mutating routes.
- [x] 4.7 Add endpoint behaviour tests in `tests/Freeboard.Web.Tests`
  (`CustomRoleEndpointTests`, using `AuthWebFactory` + fakes; drive the entitlement
  with `CustomPoliciesEntitled` from task 4.5): 404 when entitlement off (even for a
  super-admin); 403 for a non-super-admin when entitled; create/edit/delete happy
  path; excluded-key or unknown-key create returns 422; blank-title and
  over-length-title/description create returns 422; a rejected update (bad key or
  invalid title) leaves the role unchanged and records NO audit event; in-use delete
  returns 409. Extend `FakeAuthzStore`/`FakeAuthzAdministrationStore` with in-memory
  role storage for the new interface methods, recording the audit event the store
  would write.
- [x] 4.8 Add a GitOps read-only test using `AuthWebFactory { ReadOnly = true,
  CustomPoliciesEntitled = true }` (the same factory tasks 4.6-4.7 use: it already
  carries the `ReadOnly` toggle and, from task 4.5, the entitlement toggle, plus the
  authenticated-super-admin helper; `GitOpsWebFactory` has neither the entitlement
  control nor that helper). Assert a super-admin POST/PUT/DELETE on
  `/api/v1/freeboard/custom-roles` returns 409. The entitlement is ON so the 409
  proves read-only precedence over the entitlement filter (D6/D12), not a 404 from
  the entitlement gate, and confirms the authoring routes carry no `AuthEndpoint`
  exemption.
- [x] 4.9 (commit `feat(web): add the custom-role designer API`)

## 5. Web: custom-role admin page and nav (SSR)

- [x] 5.1 Add `src/Freeboard/Pages/Admin/CustomRoles.cshtml(.cs)`: SSR list of
  custom roles, a create form rendering checkboxes from the EE presentation
  catalog, per-role edit (title, description, permissions), and delete. Each
  handler checks the entitlement (`NotFound()` when off) then
  `AuthzPageGuard.CheckAsync(..., AuthzActions.SystemAdmin, new
  AuthzResource("system", null, null, []))`, calls the same store the API uses, and
  sets `Notice`.
- [x] 5.2 No extra `Program.cs` registration is needed: the `AuthorizeFolder("/Admin")`
  convention already authenticates the page. Do NOT add `/Admin/CustomRoles` to the
  `AuthEndpoint` conventions block (unlike `/Admin/RoleAssignments`), so its mutating
  POST is blocked by GitOps read-only, matching the API (D12, task 4.3).
- [x] 5.3 In `_Layout.cshtml`, show the Custom Roles nav link only when `isAdmin`
  and `IsEntitled(CustomPolicies)` (inject `IEnterpriseEntitlements`).
- [x] 5.4 Add a page test in `tests/Freeboard.Web.Tests` (funnel/page style; drive
  the entitlement with `CustomPoliciesEntitled` from task 4.5): non-admin gets 403;
  entitlement off gets 404; a super-admin create via the page handler writes an audit
  event; a page POST under GitOps read-only returns 409 (D12).
- [x] 5.5 (commit `feat(web): add the custom-role designer admin page`)

## 6. Verification

- [x] 6.1 `dotnet build` clean.
- [x] 6.2 `dotnet test` (unit/web/architecture tiers pass with no external
  dependencies; MySQL integration tests pass when `FREEBOARD_TEST_DB` is set).
- [x] 6.3 `openspec validate --strict "add-custom-role-designer"` passes.
- [x] 6.4 Confirm `EnterpriseReferenceTests` (now also pinning the forward
  direction, `Freeboard.Enterprise` -> `Freeboard.Core` only, task 3.3),
  `AuthzPlacementTests`, and `EntitlementPlacementTests` all pass (Agent/CLI carry
  no Enterprise or web-authz reference).
