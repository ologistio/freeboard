## MODIFIED Requirements

### Requirement: Super-admin authoring surface for custom organisation roles

The web app SHALL provide an Enterprise authoring surface, a server-rendered
`/settings/custom-roles` page AND a JSON API under
`/api/v1/freeboard/custom-roles`, that lets a super-admin create, edit, and delete
custom organisation-scoped roles. Every authoring route and page handler SHALL
require the `system.admin` permission, force-enforced in every rollout mode. A
custom role SHALL be created with `is_system = 0` and `scope = 'organisation'`;
the request body SHALL NOT be able to set `scope` or `is_system`. The `role_key`
SHALL be immutable after create: an edit changes only the title, description, and
permission set. The page SHALL be rendered server-side with no client-side
reactivity, and SHALL call the same authz store layer the JSON endpoints use
rather than calling the JSON API over HTTP.

Only the page route URL moves: the page file remains in the `Pages/Admin` Razor
Pages folder, so the existing `/Admin` folder authorization convention still gates
it, and the role editor route moves with it to `/settings/custom-roles/designer/{slug?}`.
The JSON API routes under `/api/v1/freeboard/custom-roles` are unchanged. The prior
`/admin/custom-roles` page URL is retired with no redirect (a deliberate clean break
in pre-release software).

#### Scenario: Super-admin creates a custom organisation role

- **WHEN** a super-admin POSTs a role with a namespaced key, a title, and an
  authorable permission set to `/api/v1/freeboard/custom-roles`
- **THEN** the role is created with `is_system = 0` and `scope = 'organisation'`
  and the response is 201

#### Scenario: Super-admin edits a custom role

- **WHEN** a super-admin PUTs a changed title and permission set for an existing
  custom role
- **THEN** the role title, description, and its `authz_role_permissions` rows are
  updated, the `role_key` is unchanged, and the response is 200

#### Scenario: Super-admin deletes a custom role

- **WHEN** a super-admin DELETEs a custom role that has no assignments
- **THEN** the role and its permission rows are removed and the response is 204

#### Scenario: Non-super-admin is forbidden

- **WHEN** an authenticated user without `system.admin` calls any authoring route
- **THEN** the response is 403 and no role is created, changed, or deleted

### Requirement: Authoring is gated on the CustomPolicies entitlement

The authoring surface SHALL be available only when the install is entitled to
`EnterpriseEntitlement.CustomPolicies`. When the entitlement is off, an authoring
request that reaches entitlement evaluation SHALL return 404, so the feature is
absent rather than forbidden, even for a super-admin. Entitlement evaluation runs
in the endpoint filter (and the page handler), AFTER `GitOpsReadOnlyMiddleware`.
So when GitOps read-only mode is on, a mutating authoring request (POST/PUT/DELETE)
SHALL be rejected by the middleware with 409 BEFORE the entitlement is read; the
entitlement-off 404 guarantee therefore holds when GitOps read-only is off, and for
GET requests in all cases (read-only does not intercept GETs). Withdrawing the
entitlement SHALL NOT affect seeded roles, already-authored custom roles, or
authorization enforcement, which read the same tables regardless of edition.

#### Scenario: Entitlement off makes authoring unavailable

- **WHEN** GitOps read-only is off, `CustomPolicies` is off, and a super-admin GETs
  `/settings/custom-roles` or calls any `/api/v1/freeboard/custom-roles` route
- **THEN** the response is 404

#### Scenario: GitOps read-only rejects a mutation before the entitlement is read

- **WHEN** GitOps read-only is on, the entitlement is on, and a super-admin POSTs,
  PUTs, or DELETEs a `/api/v1/freeboard/custom-roles` route
- **THEN** the response is 409 (the read-only middleware runs before the entitlement
  filter) and no role is created, changed, or deleted

#### Scenario: Seeded roles keep working when the entitlement is off

- **WHEN** `CustomPolicies` is off
- **THEN** the seeded roles are still assignable and authorization decisions are
  unchanged

#### Scenario: Entitlement on enables authoring

- **WHEN** `Enterprise:CustomPolicies` is set true and a super-admin GETs
  `/settings/custom-roles`
- **THEN** the page renders and the authoring routes are reachable
