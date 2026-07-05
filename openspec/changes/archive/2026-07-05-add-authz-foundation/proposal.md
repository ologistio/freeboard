## Why

Freeboard authenticates users but has only two coarse authorization gates: any
authenticated user (`RequireAuthorization()`) and one global admin role
(`RequireAuthorization(GlobalRoles.AdminPolicy)`). There is no way to say "user
U may manage organisation O and its departments but nothing else", and every
authenticated user reads the entire compliance domain regardless of which
organisation they belong to. The domain is already an organisation tree with
per-org and department-level scoping, and the codebase left an explicit seam for
this (`IOrgAccess`, "the one place a future per-organisation access model narrows
the returned subset"). We need a general authorization foundation the rest of the
app calls to answer "can principal P perform action A on resource R (in org O)?",
per-org roles as its first consumer, and - critically - the existing compliance
reads narrowed to what the caller is authorized to see, so per-org authorization
is a real boundary and not a false one.

## What Changes

- Add a small, pure authorization decision engine in `Freeboard.Core`: one
  evaluation method, deny-by-default, deny-overrides combining, fed an immutable
  request. This is the seam the app calls.
- Add a bounded policy model: a pragmatic hybrid of RBAC over persisted facts
  with a few ABAC inputs (authentication, session step-up/limited state) and one
  ReBAC relationship (org descendant-of org). No user-authored policy DSL and no
  general graph/tuple engine.
- Store roles and their permission sets as DATA, not code constants: seeded
  database tables plus fact providers. The four system roles ship seeded; the
  schema is shaped so custom or tenant-defined roles and an EE role designer are
  a later addition without a schema rewrite. Action-identifier strings remain code
  constants because endpoints reference them at call sites.
- Add the four seeded roles - `super-admin` (system-scoped), `org-owner`,
  `compliance-manager`, `compliance-reader` (org-scoped) - and their permission
  keys (`system.admin`, `authz.assignment.write`, `org.read`, `org.write`,
  `compliance.read`, `compliance.scope.write`,
  `compliance.requirement-scope.write`, `user.manage`), which map to the actual
  endpoints. An org grant applies to that org and its whole subtree via the
  existing inclusive-ancestor org walk.
- Add persistence: migration `010_authorization.sql` with data-driven tables
  (`authz_roles`, `authz_permissions`, `authz_role_permissions`,
  `authz_system_role_assignments`, `authz_organisation_role_assignments`,
  `authz_audit_events`), seeded roles/permissions, a `super-admin` backfill from
  `users.global_role='admin'`, a `compliance-reader` backfill of existing enabled
  members on the current root organisations, and a read/write store pair mirroring
  the `IComplianceStore` / `IComplianceWriteStore` convention. The migration is
  idempotent so a partially-failed re-run replays cleanly.
- Add enforcement: a `RequirePermission(action, resourceSelector)` minimal-API
  endpoint filter, a page guard (`AuthzPageGuard`) replacing `AdminGuard`, RFC
  7807 problem responses (403 for a denied action on a visible resource, 404 for
  an org-scoped resource the principal cannot see), and audit of denied decisions
  and all authz/user/admin mutations. An opted-in check fails closed; because a
  route with no filter still runs, an architecture test over route metadata asserts
  every mutating compliance, role-assignment, and user-admin route is gated.
- Gate the cross-user session routes in `AuthEndpoints` on authorization, not the
  legacy admin claim: `GET`/`DELETE /auth/sessions/{id}` and
  `GET`/`DELETE /users/{id}/sessions` serve both self-service (a caller acting on
  its OWN sessions) and a cross-user admin branch that today derives from
  `freeboard:role=admin` via `IsAdmin` -> `callerIsAdmin` in `AuthFlows`. Self-
  service stays self-only and unchanged; the cross-user branch now requires
  `user.manage` (reachable only through `system.admin`), force-enforced in every
  rollout mode and audited, so the legacy admin claim grants nothing on them. This
  closes the same revoked-super-admin gap the write gates close: a route surface
  the write-only sweep overlooked. Because a whole-route filter cannot express
  "self OR `user.manage`", the cross-user branch is force-enforced by an in-handler
  authorizer call (like `AuthzPageGuard`), and the routes carry cross-user
  permission metadata so the route-metadata architecture test covers them too.
- Narrow the existing compliance reads NOW, not later: `/organisations`,
  `/scopes`, `/requirement-scopes`, and the Statement of Applicability (both the
  JSON API and the Razor page) filter to the caller's authorized organisation
  subtree through the `IOrgAccess` seam, whose default implementation moves from
  `AllOrgAccess` to `AuthzOrgAccess`. Non-tenant catalog reads (standards,
  controls, requirements) stay authenticated-only.
- Add role-assignment management: `/api/v1/freeboard/` endpoints and an admin page
  to grant and revoke system and org-scoped roles, with last-super-admin and
  self-lockout guards. These management endpoints enforce from day one regardless
  of rollout mode.
- Add a named rollout mode `Authz:Mode = Observe | Compat | Enforce`. The engine
  is default-deny in all modes. Observe relaxes READS only: the engine logs read
  decisions and does not narrow reads (read behavior unchanged), while writes
  force-enforce in every mode. Compat
  allows legacy READ through an explicit, audited fallback (a zero-grant caller
  keeps full read); writes always require the proper permission in every mode - the
  legacy `global_role='admin'` claim no longer grants authorization, so a
  `super-admin` assignment is the sole source of system power. Enforce blocks the
  read fallback too. Ship in Compat with the backfill; flip to Enforce after tests
  and an operator role review.
- Fold the `super-admin` assignment into the `MySqlUserStore` bootstrap transaction
  and into the admin-create path (an `global_role='admin'` user created through the
  API gets its `super-admin` assignment in the same transaction). Prevent removing
  or disabling the last active super-admin, enforced atomically in the write-store
  transaction - including closing the API self-disable gap in
  `POST /users/{id}/disable`, not only the admin page guard.

## Capabilities

### New Capabilities

- `authorization-engine`: the decision seam, the policy model (RBAC over
  persisted facts plus authentication/step-up ABAC inputs, deny-overrides,
  deny-by-default), the fact-provider port, and the action-key catalog. Pure and
  MIT, in `Freeboard.Core`.
- `org-rbac`: the four seeded roles, data-driven role-to-permission mapping,
  system and org-scoped role assignments, org-hierarchy (subtree) resolution, and
  the assignment management API and admin page.
- `authz-persistence`: the six `authz_*` tables, migration 010, the seed data and
  super-admin backfill, the RESTRICT organisation foreign key with prune-before-
  delete, and the role-assignment read/write store pair.
- `authz-enforcement`: where and how decisions are enforced (endpoint filter,
  page guard, fail-closed default, 403 vs 404, audit), the read narrowing, the
  rollout mode, bootstrapping, and last-super-admin / self-disable prevention.

### Modified Capabilities

Read narrowing is in scope this increment, so three existing read specs change:

- `compliance-web-read`: the `/organisations`, `/scopes`, and
  `/requirement-scopes` reads filter to the caller's accessible organisation set;
  catalog reads stay global.
- `org-scope-selection`: the accessible organisation set becomes mode-aware.
  Under Observe it stays "all persisted organisations" for every caller (reads not
  narrowed); under Compat and Enforce it narrows to the caller's authorized subtree
  union (all for a super-admin; full audited fallback for a zero-grant caller under
  Compat, empty under Enforce).
- `statement-of-applicability`: the JSON endpoint and the page both narrow their
  node set to the caller's authorized subtree.

The compliance-write and user-admin gates change mechanism (from the admin-claim
policy to permission checks) but preserve behavior: the normative statement of
those checks lives in the new `authz-enforcement` capability, so those specs are
not modified.

## Impact

- New code: `Freeboard.Core` gains an `Authz` namespace (engine, policy model,
  fact-provider port, action catalog). `Freeboard.Persistence` gains an `Authz`
  namespace (stores, read models, migration 010, seed) plus the bootstrap and
  importer prune edits. `Freeboard` (web) gains an `Authz` namespace (the
  `IAuthorizer` seam, principal factory, endpoint filter, page guard,
  `AuthzOrgAccess`, management endpoints, one admin page).
- Modified code: `Program.cs` DI and endpoint wiring (replace the `AllOrgAccess`
  registration, add `AddAuthz`, and remove the now-dead `GlobalRoles.AdminPolicy`
  registration once compliance writes and user-admin move to permission checks);
  the admin user-management pages `Pages/Admin/Users` and `Pages/Admin/UserCredential`
  (move off `AdminGuard`/the legacy admin claim onto an `AuthzPageGuard` `user.manage`
  check), with `Pages/Admin/AdminGuard.cs` deleted (those two pages are its only
  callers); `Pages/Home.cshtml` and `Pages/Shared/_Layout.cshtml` (move the admin-link
  visibility off the legacy `freeboard:role=admin` claim onto `user.manage`/
  `system.admin` - a cosmetic, fail-safe affordance; the pages still force-enforce);
  `ComplianceEndpoints` (read filtering);
  `ComplianceWriteEndpoints` and `UserAdminEndpoints` (permission gating);
  `AuthEndpoints`/`AuthFlows` (the four cross-user session routes derive the
  cross-user branch from an `IAuthorizer` `user.manage` check instead of the
  `IsAdmin` claim; self-service is unchanged);
  `MySqlComplianceWriteStore.DeleteOrganisationAsync` and `MySqlGitOpsImporter`
  (prune authz org assignments before an organisation delete);
  `MySqlUserStore.TryBootstrapAdminAsync` and the admin-create path
  (`AuthFlows.CreateUserAsync` -> `MySqlUserStore`) (fold in the super-admin
  assignment for admin users); `StatementOfApplicability` (extract the shared
  parent-walk helper); the SoA endpoint and page (node filtering).
- MIT vs EE: the entire foundation is MIT. The engine, per-org RBAC, persistence,
  and enforcement are core capabilities, not paid features, so nothing lands in
  `Freeboard.Enterprise`. Future paid authz features (custom role designer,
  attribute-condition editor, SCIM provisioning) are the EE extension surface and
  are out of scope here.
- Cross-platform: `Freeboard.Agent` and `Freeboard.CLI` reference no Enterprise
  assembly and wire no web authz enforcement (nothing from `Freeboard/Authz`);
  enforcement is web-only, matching how the CLI administers via the HTTP API.
  `Freeboard.CLI` already references `Freeboard.Persistence`, so the MIT Core authz
  engine and the Persistence authz stores are present transitively in its build
  output; that is expected and is not a violation - the invariant is no Enterprise
  reference and no authz-enforcement wiring, not the absence of authz store types.
- Data: additive migration with seed rows and a super-admin backfill; the
  organisation foreign key is `ON DELETE RESTRICT`, so the org-delete and GitOps
  import paths prune authz assignment rows before deleting an organisation.
