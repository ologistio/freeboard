## ADDED Requirements

### Requirement: Custom-role read and write operations on the authz stores

The authz read store (`IAuthzStore`) SHALL expose operations to list custom roles
and to load a single role with its permission keys. The authz write store
(`IAuthzAdministrationStore`) SHALL expose operations to create, update, and delete
a custom role, each returning the existing `AuthzWriteResult` so the endpoint layer
maps outcomes to status codes. No new database migration SHALL be introduced:
custom roles reuse the `authz_roles`, `authz_permissions`, and
`authz_role_permissions` tables from migration `010_authorization.sql`, with
`is_system = 0`. A create SHALL insert the role row and its
`authz_role_permissions` rows in a single transaction; a duplicate `role_key` SHALL
return a conflict.

#### Scenario: Create then read a custom role

- **WHEN** a custom organisation-scoped role is created and then loaded by key
- **THEN** the role is returned with `is_system = 0`, `scope = 'organisation'`, and
  its composed permission keys

#### Scenario: Duplicate role key is a conflict

- **WHEN** a role is created with a `role_key` that already exists
- **THEN** the write returns a conflict result and no second row is written

#### Scenario: List returns custom roles only

- **WHEN** the custom-role list is loaded
- **THEN** it returns the `is_system = 0` roles and does not include the seeded
  `is_system = 1` roles as editable entries

### Requirement: The write store enforces the custom-role security floor

The write store SHALL enforce, independently of any entitlement, that a custom role
composes only authorable permissions and is always organisation-scoped. The create
operation SHALL NOT accept a caller-supplied `scope` or `is_system`; it SHALL
always persist `scope = 'organisation'` and `is_system = 0`, so the store can never
mint a system-scoped or otherwise mis-scoped custom role. A create or update SHALL
validate every submitted permission key against the `Freeboard.Core` authorable
allow-list (`org.read`, `org.write`, `compliance.read`, `compliance.scope.write`,
`compliance.requirement-scope.write`) and SHALL return an invalid result and write
nothing when any key is outside it. Because the privileged keys `system.admin`,
`user.manage`, and `authz.assignment.write` are not in the allow-list, they are
rejected by this single positive check. The store SHALL also validate that `title`
is non-blank and at most 190 characters. `description` is optional: the store SHALL
coerce a null or omitted value to an empty string (so the `NOT NULL`
`authz_roles.description` column is never violated) and SHALL reject only a value
longer than 512 characters (the `authz_roles.title` and `authz_roles.description`
column widths), returning an invalid result and writing nothing otherwise. This
floor SHALL hold even when the caller is a super-admin and even when the entitlement
gate is bypassed.

#### Scenario: Created custom role is always organisation-scoped

- **WHEN** a custom role is created
- **THEN** it is persisted with `scope = 'organisation'` and `is_system = 0`, and
  no caller input can set a different scope or make it a system role

#### Scenario: Blank or over-length title or description is rejected

- **WHEN** a create or update supplies a blank title, a title longer than 190
  characters, or a description longer than 512 characters
- **THEN** the write returns an invalid result and writes nothing

#### Scenario: Rejected update leaves the role and audit trail unchanged

- **WHEN** an update to an existing custom role is rejected (a non-authorable
  permission key or an invalid title)
- **THEN** the role's title, description, and `authz_role_permissions` rows are
  unchanged and no `authz_audit_events` row is written

#### Scenario: Excluded permission key is rejected by the store

- **WHEN** a create or update includes `system.admin`, `user.manage`, or
  `authz.assignment.write` in the permission set
- **THEN** the write returns an invalid result and no role or permission row is
  written or changed

#### Scenario: Unknown permission key is rejected by the store

- **WHEN** a create or update includes a permission key that is not in the Core
  authorable allow-list
- **THEN** the write returns an invalid result and no role or permission row is
  written or changed

### Requirement: Custom role keys are namespaced by a reserved prefix

A custom `role_key` SHALL begin with the reserved prefix `custom:` defined in
`Freeboard.Core`, followed by a bounded lowercase ASCII hyphenated slug, with the
full key no longer than 64 characters (the `authz_roles.role_key` column width).
The write store SHALL reject a create whose `role_key` does not satisfy this rule.
Seeded system roles SHALL NOT use the prefix, so a custom key can never collide
with or shadow a seeded key.

#### Scenario: Unprefixed custom key is rejected

- **WHEN** a create requests a `role_key` that does not start with `custom:`
- **THEN** the write returns an invalid result and writes nothing

#### Scenario: Seeded keys do not use the reserved prefix

- **WHEN** the seeded role keys are inspected
- **THEN** none begins with `custom:`

### Requirement: Seeded roles are immutable and in-use roles cannot be deleted

The write store SHALL reject an update or delete that targets a seeded role
(`is_system = 1`). An update of a custom role SHALL change only the title,
description, and permission set; it SHALL NOT change `role_key`, `scope`, or
`is_system`. The write store SHALL reject a delete of a custom role that has any
live assignment, returning a conflict and deleting nothing; the assignment foreign
keys (`ON DELETE RESTRICT`) are the database backstop, and an FK-restrict failure
racing the assignment count SHALL be mapped to a conflict. A delete of a custom
role with no assignments SHALL remove the role and cascade its
`authz_role_permissions` rows.

#### Scenario: Seeded role cannot be edited or deleted

- **WHEN** an update or delete targets a seeded role such as `org-owner`
- **THEN** the write is rejected and the seeded role is unchanged

#### Scenario: Update does not change the role key or scope

- **WHEN** an update targets an existing custom role
- **THEN** only the title, description, and permission set change, and the
  `role_key`, `scope`, and `is_system` are unchanged

#### Scenario: In-use custom role cannot be deleted

- **WHEN** a delete targets a custom role that has at least one assignment
- **THEN** the write returns a conflict and the role is not deleted

#### Scenario: Unused custom role is deleted with its permissions

- **WHEN** a delete targets a custom role with no assignments
- **THEN** the role row and its `authz_role_permissions` rows are removed
