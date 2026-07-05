## ADDED Requirements

### Requirement: Four seeded roles map to permission sets as data

The system SHALL seed four roles and their permission sets as data (not code): a
system-scoped `super-admin` and three organisation-scoped roles `org-owner`,
`compliance-manager`, and `compliance-reader`. Each role's scope (`system` or
`organisation`) SHALL be persisted on the role, so it is a validated invariant rather
than a naming convention. The mapping SHALL be:

- `super-admin` -> `system.admin`.
- `org-owner` -> `org.read`, `org.write`, `compliance.read`,
  `compliance.scope.write`, `compliance.requirement-scope.write`,
  `authz.assignment.write`.
- `compliance-manager` -> `org.read`, `compliance.read`, `compliance.scope.write`,
  `compliance.requirement-scope.write`.
- `compliance-reader` -> `org.read`, `compliance.read`.

`user.manage` SHALL NOT belong to any seeded role and SHALL be reachable only
through `system.admin`. The schema SHALL allow additional (custom) roles and
role-to-permission rows to be added later without altering the tables.

#### Scenario: Owner role maps to its permission set

- **WHEN** the effective permissions of `org-owner` are resolved
- **THEN** they include `org.write`, `compliance.scope.write`,
  `compliance.requirement-scope.write`, and `authz.assignment.write`

#### Scenario: Reader role is read-only

- **WHEN** the effective permissions of `compliance-reader` are resolved
- **THEN** they include `org.read` and `compliance.read` and no write permission

#### Scenario: user.manage is not held by any org role

- **WHEN** the effective permissions of `org-owner`, `compliance-manager`, or
  `compliance-reader` are resolved
- **THEN** none of them includes `user.manage`

### Requirement: Roles are assigned to a user, system-wide or scoped to an organisation

The system SHALL let `super-admin` be assigned to a user system-wide, and let an
organisation-scoped role be assigned to a user scoped to a single organisation,
forming a grant `(user, role, organisation)`. A user MAY hold different roles on
different organisations. The same system assignment or the same
`(user, role, organisation)` grant SHALL exist at most once.

#### Scenario: A user is granted an org-scoped role

- **WHEN** a user is granted `org-owner` on organisation O
- **THEN** the grant is persisted and readable as a grant of that user on O

#### Scenario: A user is granted the system super-admin role

- **WHEN** a user is granted `super-admin` system-wide
- **THEN** the assignment is persisted and the user holds `system.admin`
  everywhere

#### Scenario: Duplicate grant is rejected

- **WHEN** the same user is granted the same role on the same organisation twice
- **THEN** the second attempt is rejected and the store is unchanged

### Requirement: A grant applies to the organisation subtree by inclusive-ancestor match

A grant on an organisation SHALL apply to that organisation and to all of its
descendants. When deciding an action on a resource, the system SHALL resolve the
resource organisation's inclusive ancestry (the organisation itself and each
parent up to a root, guarding against cycles) and SHALL treat the action as
permitted when the principal holds a grant on any organisation in that ancestry
whose role permits the action.

#### Scenario: Grant on a parent covers a descendant resource

- **WHEN** a user holds `org-owner` on a company O and acts on a resource owned by
  a department D that is a descendant of O
- **THEN** the action is permitted through the inclusive-ancestor match

#### Scenario: Grant on a department does not cover a sibling

- **WHEN** a user holds `org-owner` only on department D1 and acts on a resource
  owned by sibling department D2
- **THEN** the action is not permitted by RBAC

#### Scenario: Ancestry resolution terminates on a cyclic parent link

- **WHEN** resolving a resource organisation's ancestry where a parent link forms
  a cycle
- **THEN** resolution terminates via a visited-set guard and yields a finite
  ancestry

### Requirement: Role assignments are managed through the API and an admin page

The web app SHALL provide endpoints under `/api/v1/freeboard/` to grant and revoke
role assignments, and an admin page to view and manage them, with concrete contracts
consistent with the existing `/api/v1/freeboard/` endpoints (JSON bodies, RFC 7807
problem responses):

- `GET /api/v1/freeboard/organisations/{orgId}/role-assignments` lists the
  organisation-scoped assignments on that organisation and returns 200 with a JSON
  array of `{ user_id, role_key, organisation_id, created_at }`.
- `PUT /api/v1/freeboard/organisations/{orgId}/role-assignments` grants an
  organisation-scoped role, body `{ user_id, role_key }`, and returns 201 on create
  or 409 when the grant already exists.
- `DELETE /api/v1/freeboard/organisations/{orgId}/role-assignments/{userId}/{roleKey}`
  revokes an organisation-scoped role and returns 204 on success, 404 when the grant
  does not exist, or 409 when the revoke would remove the last direct `org-owner`.
- `GET /api/v1/freeboard/system-role-assignments` lists `super-admin` holders (200).
- `PUT /api/v1/freeboard/system-role-assignments` grants `super-admin`, body
  `{ user_id }`, returning 201 or 409.
- `DELETE /api/v1/freeboard/system-role-assignments/{userId}` revokes `super-admin`
  and returns 204, 404 when not held, or 409 when it would remove the last active
  `super-admin`.

Granting or revoking an organisation-scoped role SHALL require the
`authz.assignment.write` permission on the target organisation (or `system.admin`);
granting or revoking the system `super-admin` role SHALL require `system.admin`. A
caller who cannot see the target organisation at all SHALL receive 404 (existence
non-disclosure), while a visible-organisation caller who lacks the permission SHALL
receive 403. The organisation-scoped grant endpoint SHALL reject a `role_key` whose
role scope is `system` (and the system endpoint accepts only `super-admin`) with the
validation-failure status, so a `system`-scoped role can never be granted through the
org-scoped route. These management endpoints SHALL enforce authorization regardless of
the rollout mode.

#### Scenario: Owner grants a reader role

- **WHEN** an `org-owner` of O grants `compliance-reader` to a user on O
- **THEN** the grant is created and appears on the management page

#### Scenario: A caller without authz.assignment.write is denied

- **WHEN** a user without `authz.assignment.write` on O (and not a super-admin)
  tries to grant or revoke a role on O
- **THEN** the request is denied with a problem response and no grant changes

#### Scenario: Assigning the system role requires system.admin

- **WHEN** a caller without `system.admin` tries to grant `super-admin`
- **THEN** the request is denied and no assignment is created

#### Scenario: A system-scoped role cannot be granted through the org-scoped route

- **WHEN** a caller with `system.admin` attempts to grant `super-admin` through the
  organisation-scoped role-assignment endpoint
- **THEN** the request is rejected with the validation-failure status and no
  organisation assignment is created

### Requirement: Grant loading is efficient for a decision

Deciding an action SHALL NOT issue a query per candidate resource or per ancestry
level. The system SHALL load a principal's effective permissions and organisation
grants in a bounded number of queries once per request, and resolve organisation
ancestry from a single organisation read, both memoized for the request.

#### Scenario: Bounded fact load per request

- **WHEN** several authorization decisions are made within one request for the same
  principal
- **THEN** the principal's facts are loaded at most once in a bounded number of
  queries and organisation ancestry is resolved from a single organisation read
