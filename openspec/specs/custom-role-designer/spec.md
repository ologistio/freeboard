# custom-role-designer Specification

## Purpose
TBD - created by archiving change add-custom-role-designer. Update Purpose after archive.
## Requirements
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

### Requirement: Only org-tree read/write permissions are authorable

The enforced authorable permission set SHALL be defined in `Freeboard.Core` (so
the MIT write store can validate against it independently of the entitlement) and
SHALL contain exactly the organisation-tree read/write keys: `org.read`,
`org.write`, `compliance.read`, `compliance.scope.write`, and
`compliance.requirement-scope.write`. `Freeboard.Enterprise` SHALL hold only
presentation metadata (labels, descriptions, groups) over that Core allow-list;
the presentation catalog SHALL be a subset of the Core allow-list and SHALL NOT
introduce any key outside it. The Enterprise catalog SHALL reference
`Freeboard.Core` only and SHALL NOT reference the web or persistence projects. The
authoring page SHALL offer only these keys, and the authoring endpoint SHALL
reject a submitted set that contains any key outside the Core allow-list before
writing.

#### Scenario: Authorable allow-list is the org-tree read/write keys

- **WHEN** the Core authorable organisation-scope allow-list is inspected
- **THEN** it is exactly `org.read`, `org.write`, `compliance.read`,
  `compliance.scope.write`, `compliance.requirement-scope.write`

#### Scenario: Presentation catalog lives in the Enterprise assembly and is Core-only

- **WHEN** the Enterprise presentation-catalog type is inspected
- **THEN** it resides in the `Freeboard.Enterprise` assembly, depends on no web or
  persistence type, and covers exactly the Core allow-list with no extra key

### Requirement: Excluded permission keys are never authorable

The authoring surface SHALL never allow `system.admin`, `user.manage`, or
`authz.assignment.write` in a custom role. A create or edit whose permission set
contains any of these keys SHALL be rejected with an unprocessable-entity error and
SHALL write nothing.

#### Scenario: Excluded key is rejected

- **WHEN** a super-admin submits a custom role whose permission set includes
  `authz.assignment.write` (or `system.admin`, or `user.manage`)
- **THEN** the response is 422 and no role or permission row is written

### Requirement: Title and description are validated against the column widths

The authoring endpoint and the write store SHALL validate the role `title` and
`description` before writing. `title` SHALL be required: non-blank and at most 190
characters (the `authz_roles.title` column width). `description` SHALL be optional:
a null or omitted value SHALL be stored as an empty string (so the
`authz_roles.description NOT NULL` column is never violated), and a value longer
than 512 characters SHALL be rejected. A create or edit that violates the title
rule or supplies an over-length description SHALL be rejected with an
unprocessable-entity error and SHALL write nothing.

#### Scenario: Blank title is rejected

- **WHEN** a super-admin submits a create or edit with a blank (empty or
  whitespace) title
- **THEN** the response is 422 and no role or permission row is written or changed

#### Scenario: Omitted description is stored as an empty string

- **WHEN** a super-admin submits a create or edit with a null or omitted description
- **THEN** the write succeeds and the stored description is an empty string

#### Scenario: Over-length title or description is rejected

- **WHEN** a super-admin submits a title longer than 190 characters or a
  description longer than 512 characters
- **THEN** the response is 422 and no role or permission row is written or changed

### Requirement: Every authoring mutation writes an audit event atomically

Each successful create, edit, and delete SHALL insert one `authz_audit_events` row
recording the actor, the action, `resource_type = "authz_role"`, and the role key,
INSIDE the same database transaction as the role mutation. A committed mutation
SHALL always carry its audit row, and a rolled-back mutation SHALL leave no audit
row; the audit write SHALL NOT be a separate best-effort append that can leave a
committed mutation without an audit row.

#### Scenario: Create writes an audit row in the same transaction

- **WHEN** a super-admin successfully creates a custom role
- **THEN** an `authz_audit_events` row committed with the mutation records the
  create event type and the role key as the resource id

#### Scenario: Delete writes an audit row

- **WHEN** a super-admin successfully deletes a custom role
- **THEN** an `authz_audit_events` row committed with the mutation records the
  delete event type

#### Scenario: Rejected mutation writes no audit row and changes nothing

- **WHEN** an edit is rejected (an excluded or unknown permission key, or an
  invalid title) for an existing custom role
- **THEN** the role's title, description, and `authz_role_permissions` rows are
  unchanged and no `authz_audit_events` row is written

### Requirement: Authoring routes force-enforce and carry route metadata

Every mutating authoring route SHALL carry the authz permission metadata with
`AlwaysEnforce = true`, so a deny blocks in every rollout mode and the
route-metadata architecture test covers it. A new mutating authoring route that is
not force-enforced SHALL fail the route-metadata test.

#### Scenario: Mutating role routes are force-enforced

- **WHEN** the route metadata for POST, PUT, and DELETE on the `custom-roles`
  surface is inspected
- **THEN** each carries the `system.admin` permission requirement with
  `AlwaysEnforce = true`

