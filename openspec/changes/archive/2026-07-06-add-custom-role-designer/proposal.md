## Why

Operators can only assign the four seeded roles; they cannot express an
organisation-scoped role that composes a subset of the org-tree permission keys
(for example "read plus standard-level scope write, no requirement-scope write").
Epic 1 sells custom authorization policies as an Enterprise feature. The authz
foundation (change `add-authz-foundation`) already models roles as data
(`authz_roles.is_system`, `authz_roles.scope`, `authz_role_permissions`) and the
entitlement seam (change `add-enterprise-entitlement-seam`) already ships the
`CustomPolicies` gate, so the remaining work is an authoring surface over data
that already exists.

## What Changes

- Add a super-admin authoring surface (admin page plus JSON API) to create, edit,
  and delete custom organisation-scoped roles: `authz_roles` rows with
  `is_system = 0`, `scope = 'organisation'`, composed from existing
  `authz_permissions` keys via `authz_role_permissions`.
- Restrict authorable permissions to the org-tree read/write keys: `org.read`,
  `org.write`, `compliance.read`, `compliance.scope.write`,
  `compliance.requirement-scope.write`. The enforced allow-list lives in
  `Freeboard.Core` (MIT) so the persistence write store can validate against it;
  `Freeboard.Enterprise` (the paid carve-out) adds only presentation metadata
  (labels, descriptions, groups) over that Core list for the designer surface.
- Make the MIT persistence write store validate every submitted permission key
  against the Core allow-list (which excludes `system.admin`, `user.manage`, and
  `authz.assignment.write` by construction) and validate `title`/`description`
  against the column widths. The create carries no caller-settable `scope` or
  `is_system`: the store always writes `scope = 'organisation'`, `is_system = 0`,
  so it can never mint a non-organisation or system custom role. This is the
  security floor and does not depend on the entitlement.
- Namespace custom `role_key` values with a reserved `custom:` prefix plus a
  bounded slug so a custom key can never collide with or shadow a seeded system
  key, and keep `role_key` immutable after create (rename is delete-then-recreate
  while unused).
- Block deletion of a role that has live assignments; keep seeded (`is_system = 1`)
  roles read-only and undeletable.
- Write an `authz_audit_events` row for every create, edit, and delete, inside the
  same transaction as the mutation so a committed mutation always carries its
  audit row.
- Gate the authoring endpoints and page on the `CustomPolicies` entitlement: gate
  off makes authoring return 404, while seeded roles and all enforcement keep
  working unchanged.

## Capabilities

### New Capabilities

- `custom-role-designer`: the Enterprise authoring surface (admin page plus JSON
  API) for custom organisation-scoped roles, its super-admin gate, force-enforced
  route metadata, `CustomPolicies` entitlement gate, audit writes, and the
  `Freeboard.Enterprise` presentation catalog over the Core authorable allow-list.

### Modified Capabilities

- `authz-persistence`: add custom-role read and write operations to the MIT authz
  stores, with the security-floor invariants (excluded permission keys rejected,
  `title`/`description` validated, always organisation-scoped, reserved `role_key`
  prefix, in-use-delete protection, seeded-role immutability) enforced in the
  persistence layer.

## Impact

- MIT: `Freeboard.Core` (new `AuthzCustomRoles`: reserved key prefix, authorable
  permission allow-list, and key-format rule), `Freeboard.Persistence`
  (`IAuthzStore`, `IAuthzAdministrationStore`, `MySqlAuthzStore`,
  `MySqlAuthzAdministrationStore`, `AuthzReadModels`). No new migration: the
  existing `010_authorization.sql` schema already supports custom roles.
- EE: `Freeboard.Enterprise` gains a presentation catalog (labels, descriptions,
  groups) over the Core allow-list; references `Freeboard.Core` only.
- Web (`Freeboard`): new custom-role JSON endpoints, a `/admin/custom-roles` Razor
  page, a reusable entitlement endpoint filter, and a nav link. Consumes the
  existing `IEnterpriseEntitlements` gate.
- Tests: `Freeboard.Persistence.Tests` (MySQL integration for the write store),
  `Freeboard.Web.Tests` (gating, force-enforce metadata, endpoint behaviour, GitOps
  read-only 409, entitlement toggle on `AuthWebFactory`),
  `Freeboard.Architecture.Tests` (`EnterpriseReferenceTests` extended to pin
  `Freeboard.Enterprise` -> `Freeboard.Core` only; route-metadata coverage via the
  Web.Tests route-metadata test).
- Non-goals: see design.md.
