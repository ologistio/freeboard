# authz-persistence Specification

## Purpose
TBD - created by archiving change add-authz-foundation. Update Purpose after archive.
## Requirements
### Requirement: Authorization data lives in six tables added by migration 010

The system SHALL persist authorization data in six tables added by migration
`010_authorization.sql`, following the embedded `Migrations/NNN_slug.sql`
convention so it is auto-discovered and ordered after `009`:

- `authz_roles(role_key PK, title, description, scope, is_system, created_at,
  updated_at)`, where `scope` is `system` or `organisation` and is a persisted
  invariant that constrains which assignment table the role may be written to.
- `authz_permissions(permission_key PK, resource_type, description, is_system)`.
- `authz_role_permissions(role_key, permission_key)` with a composite primary key.
- `authz_system_role_assignments(user_id, role_key)` with a composite primary key.
- `authz_organisation_role_assignments(user_id, role_key, organisation_id)` with a
  composite primary key.
- `authz_audit_events` with a primary-key id, an occurred-at timestamp, an event
  type, and scalar actor/action/resource/organisation/effect/reason columns.

Id and foreign-key columns SHALL use the `utf8mb4_bin` collation to match the
exact-byte id identity used across the schema.

#### Scenario: Migration applies on a fresh database

- **WHEN** the migration runner applies all migrations to a fresh database
- **THEN** the six `authz_*` tables exist with their primary keys

#### Scenario: Duplicate assignment is rejected by the primary key

- **WHEN** two rows with the same `(user_id, role_key, organisation_id)` are
  inserted into `authz_organisation_role_assignments`
- **THEN** the second insert is rejected by the primary key

### Requirement: The migration seeds the four roles and backfills super-admins

Migration 010 SHALL seed the four system roles (`super-admin`, `org-owner`,
`compliance-manager`, `compliance-reader`) with `is_system=1` and their `scope`
(`super-admin`=`system`; `org-owner`, `compliance-manager`, `compliance-reader`=
`organisation`), the eight permissions, and the role-to-permission rows for each role. It SHALL backfill a
`super-admin` system assignment for every existing user whose `global_role` is
`admin`, so existing administrators remain administrators after the change. It SHALL
also backfill a `compliance-reader` assignment on the current ROOT organisations for
every existing ENABLED non-admin user, so a root grant covers the whole tree and the
Enforce flip hides nothing from existing members.

The migration SHALL be re-runnable, mirroring the re-runnable-migration convention,
because the runner replays a migration that failed partway (its version stays
unrecorded on a partial failure). It SHALL use `CREATE TABLE IF NOT EXISTS` for all
six tables and `INSERT ... ON DUPLICATE KEY UPDATE` (or `INSERT IGNORE`) for every
seed and backfill row (roles, permissions, role-permissions, the super-admin
backfill, and the member backfill), so a re-run neither duplicates a row nor errors.

#### Scenario: Seeded roles and permissions exist after migration

- **WHEN** the migration has applied
- **THEN** the four roles, the eight permissions, and their role-to-permission
  rows are present and marked `is_system`, with `super-admin` scoped `system` and the
  three organisation roles scoped `organisation`

#### Scenario: Existing global admins become super-admins

- **WHEN** the migration applies to a database with a user whose `global_role` is
  `admin`
- **THEN** that user has a `super-admin` system role assignment

#### Scenario: Existing enabled members become compliance-readers on the roots

- **WHEN** the migration applies to a database with an enabled non-admin user
- **THEN** that user has a `compliance-reader` assignment on each current root
  organisation

#### Scenario: Re-running the migration is idempotent

- **WHEN** the migration file is replayed after a partial failure
- **THEN** every table, seed row, and backfill row is created or left unchanged with
  no duplicate-key error

### Requirement: Foreign keys cascade on subject deletes but restrict on organisation

The `authz_role_permissions` role foreign key SHALL be `ON DELETE CASCADE` and its
permission foreign key `ON DELETE RESTRICT`. Both assignment tables' `user_id`
foreign key SHALL be `ON DELETE CASCADE` and their `role_key` foreign key `ON
DELETE RESTRICT`. The `organisation_id` foreign key on
`authz_organisation_role_assignments` SHALL be `ON DELETE RESTRICT`, matching the
schema convention. The `authz_audit_events` actor and resource columns SHALL be
plain scalars with no foreign keys, so audit history survives the deletion of the
referenced user or organisation.

#### Scenario: Deleting a user removes its assignments

- **WHEN** a user with role assignments is deleted
- **THEN** all of that user's system and organisation role assignments are removed

#### Scenario: Organisation delete is restricted while an assignment exists

- **WHEN** an organisation still has role assignments and a delete is attempted
  without pruning them
- **THEN** the foreign key restricts the delete

#### Scenario: Audit history survives a deleted subject

- **WHEN** a user or organisation referenced by an audit event is deleted
- **THEN** the audit event row remains with its scalar ids intact

### Requirement: Org delete and GitOps import prune assignments before deleting an organisation

The app-managed organisation-delete path and the GitOps importer SHALL prune
`authz_organisation_role_assignments` for the affected organisation before deleting
that organisation, inside the same transaction, because the organisation foreign key
is `ON DELETE RESTRICT`. This ensures an organisation delete is never blocked by an
authorization assignment and the importer needs no knowledge of role semantics.

#### Scenario: App-managed org delete prunes then deletes

- **WHEN** an organisation with role assignments is deleted through the write path
- **THEN** its assignment rows are pruned first and the organisation is deleted

#### Scenario: GitOps import prunes assignments for absent organisations

- **WHEN** an import removes an organisation that has role assignments
- **THEN** the importer prunes that organisation's assignment rows before deleting
  it and the import is not blocked

### Requirement: Read and write stores follow the store split with bounded fact loading

The system SHALL provide a read store and a write store for authorization data,
mirroring the compliance read/write split. The read store SHALL load a principal's
effective permissions and organisation grants in a bounded number of queries (no
per-request N+1), list system and organisation assignments for the management UI,
and return read-model records; a decision that spans multiple tables SHALL use a
`RepeatableRead` snapshot. Fact loading SHALL defensively ignore a mis-scoped
assignment row - a `system`-scoped role found in the organisation assignment table,
or an `organisation`-scoped role found in the system assignment table - by joining on
`authz_roles.scope`, so a stray row can never contribute `system.admin` (permit-all)
to a principal's facts. The write store SHALL create and revoke assignments and
append audit events, return a `WriteResult`, validate that the role is known and the
referenced user and organisation exist before writing, validate the role's `scope`
against the assignment table (rejecting a `system`-scoped role through the
organisation-role write and an `organisation`-scoped role through the system-role
write with the validation-failure result, writing nothing), map a concurrent
duplicate to a conflict result rather than a raw driver error, and enforce the
last-super-admin and last-owner guards. The last-super-admin and last-owner guards
SHALL be enforced inside the single locking transaction that performs the revoke or
disable - a `SELECT ... FOR UPDATE` over the active assignments/users, or a
conditional `UPDATE`/`DELETE` whose `WHERE` carries the count guard so it affects
zero rows when the guard would be violated - so two concurrent revokes or disables
cannot both pass and zero out the last holder. An "active super-admin" (a USABLE
administrator) is a user that is enabled, holds `super-admin`, AND has an
authentication credential, so a super-admin that cannot authenticate is never counted
as the surviving last admin; the guard's count and lock therefore join the credential
table. The "last org-owner" is the last DIRECT `org-owner` assignment on the
organisation (not one effective through an ancestor grant). Both stores SHALL be
registered as singletons through an `AddAuthz` persistence extension that shares the
single connection factory.

#### Scenario: Read store loads a principal's facts in bounded queries

- **WHEN** the read store loads a principal's effective permissions and grants
- **THEN** it returns them as read-model records in a bounded number of queries

#### Scenario: Write store rejects an unknown role

- **WHEN** the write store is asked to assign a role that is not seeded
- **THEN** it returns a failed `WriteResult` and writes nothing

#### Scenario: Write store rejects a mis-scoped grant

- **WHEN** a `system`-scoped role (for example `super-admin`) is assigned through the
  organisation-role write, or an `organisation`-scoped role through the system-role
  write
- **THEN** the write returns the validation-failure result and writes nothing

#### Scenario: Fact loader ignores a mis-scoped assignment row

- **WHEN** a stray `system`-scoped role row exists in the organisation assignment
  table (or vice versa) and a principal's facts are loaded
- **THEN** the mis-scoped row is dropped by the scope join and does not contribute
  its permissions (in particular never `system.admin`)

#### Scenario: Concurrent duplicate maps to a conflict

- **WHEN** a concurrent insert races the pre-check and the primary key rejects it
- **THEN** the write surfaces a conflict result, not a raw database error

#### Scenario: Concurrent last-holder revokes cannot both pass

- **WHEN** two revokes of the last active `super-admin` (or last direct `org-owner`)
  run concurrently against the write store
- **THEN** the in-transaction guard permits at most one and rejects the other, so a
  holder always remains

#### Scenario: Concurrent disables of the last two usable super-admins cannot both pass

- **WHEN** the last two usable super-admins are disabled concurrently, or one is
  revoked while the other is disabled concurrently
- **THEN** the in-transaction guard lets at most one mutation proceed, so at least one
  usable super-admin always remains

#### Scenario: A super-admin without a credential is not counted as the last admin

- **WHEN** a super-admin assignment exists for a user that has no authentication
  credential and the sole usable super-admin is revoked or disabled
- **THEN** the guard rejects the mutation, because the credential-less super-admin is
  not a usable administrator

#### Scenario: A failed credential write during admin create leaves no super-admin

- **WHEN** an admin (`global_role='admin'`) user is created and the credential write
  fails
- **THEN** the whole create rolls back and no `super-admin` assignment remains, so no
  orphan super-admin is counted

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

