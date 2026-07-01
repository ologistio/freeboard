## MODIFIED Requirements

### Requirement: MySQL-backed store for the compliance domain

The system SHALL provide a MySQL-backed store that persists the compliance domain
(`Standard`, `Control`, `Organisation`, `Scope`) and their cross-references. The
store SHALL live in a dedicated MIT project (`Freeboard.Persistence`) that holds
the MySQL client dependency, and SHALL NOT add any database or socket dependency to
`Freeboard.Core`. The store is the general compliance data layer; GitOps `sync` is
one writer into it (see the GitOps importer requirement) and is not part of the
store's identity. Each resource SHALL be stored keyed on its immutable `id`;
`title` is a mutable column and SHALL NOT be part of any key or match. Each
resource row SHALL also persist its `api_version`, a `created_at` set on first
insert, and an `updated_at` set on every write.

#### Scenario: Domain persists keyed on id

- **WHEN** a validated config is written to the store
- **THEN** each `Standard`, `Control`, `Organisation`, and `Scope` is stored as a
  row whose primary key is its `id`, with `title`, `api_version`, `created_at`, and
  `updated_at` columns

#### Scenario: Store dependency stays out of Core

- **WHEN** the persistence project and the MySQL client are added
- **THEN** the `Freeboard.Core` assembly still references no
  `System.Net.Http` or `System.Net.Sockets` types, and `Freeboard.Core` gains no
  reference to the persistence project or any database client

### Requirement: Cross-references persisted as relations

The store SHALL persist `Control.maps_to` (Standard ids) as relational rows with
foreign keys to the referenced standard by `id`, not as denormalized text.
`Organisation.parent` SHALL be persisted as a nullable self-referential foreign key
on the organisation row. `Scope.organisation` and `Scope.standard` SHALL be
persisted as foreign-key columns on the scope row referencing the organisation and
standard by `id`. Referential integrity SHALL be enforced by the database. Reads
SHALL return the references resolved by `id`.

#### Scenario: maps_to stored as a relation

- **WHEN** a `Control` with two `maps_to` Standard ids is written
- **THEN** two relation rows link the control id to each standard id, each with a
  foreign key to the standards table

#### Scenario: Organisation parent stored as a self-FK

- **WHEN** an `Organisation` with a `parent` is written
- **THEN** its row holds a `parent_id` foreign key referencing the organisations
  table, and a root organisation stores a null `parent_id`

#### Scenario: Scope stored with organisation and standard foreign keys

- **WHEN** a `Scope` mapping an organisation to a standard with a disposition is
  written
- **THEN** its row holds `organisation_id` and `standard_id` foreign keys and a
  `disposition` column

### Requirement: General read store and GitOps importer abstractions

The system SHALL expose separate abstractions for reading the store and for
importing config into it. `IComplianceStore` (the general read abstraction, in the
`Freeboard.Persistence` namespace) SHALL provide read methods returning the
persisted standards, controls, organisations (with resolved `parent`), and scopes
(with resolved `organisation`, `standard`, and `disposition`) and per-kind counts.
`IGitOpsImporter` (in the `Freeboard.Persistence.GitOps` namespace - GitOps is one
writer into the general store) SHALL provide a method that replaces the persisted
set from an already-validated `GitOpsConfig`. `IGitOpsImporter.ImportAsync` SHALL
document that its caller guarantees the config has been validated; the importer
SHALL NOT re-run Core validation. The web app's dependency-injection registration
SHALL register `IComplianceStore` for reads, so the web app's service provider does
not resolve `IGitOpsImporter` or `IMigrationRunner`. The MySQL implementations SHALL
satisfy these abstractions. Consumers SHALL depend on the abstractions, not the
concrete implementations.

#### Scenario: Read returns persisted domain

- **WHEN** a caller invokes the `IComplianceStore` read methods after an import
- **THEN** it receives the persisted standards, controls, organisations, and scopes
  with their `id`, `title`, and resolved references

#### Scenario: Import replaces the persisted set

- **WHEN** a caller imports an already-validated `GitOpsConfig` via `IGitOpsImporter`
- **THEN** the store reflects exactly that config: resources present by `id` are
  upserted, and resources whose `id` is no longer present are removed

#### Scenario: Web registration resolves only the reader

- **WHEN** the web app's service provider is built from its read-path DI registration
- **THEN** it resolves `IComplianceStore` and does NOT resolve `IGitOpsImporter`
  or `IMigrationRunner`

### Requirement: Import order is FK-safe and replaces the whole persisted set

The importer SHALL run in a fixed order within one DML transaction: upsert all
domain rows by `id` in FK-safe order (standards; controls; organisations
parent-before-child; then scopes, whose rows reference organisations and
standards); then replace all `maps_to` cross-ref join rows for the imported set
(delete the existing join rows and insert the rows derived from the new config, a
whole-set replacement rather than a per-parent diff); then delete domain rows whose
`id` is absent from the config in FK-safe order (scopes; then organisations
child-before-parent; then controls; then standards). Because a validated config is
acyclic and has no dangling references (a `Freeboard.Core` invariant), a stable
order exists and foreign-key constraints hold at commit.

#### Scenario: Dropping a referenced standard in the same sync succeeds

- **WHEN** a sync removes a Standard that, in the prior persisted state, was
  referenced by a Control via `maps_to` or by a Scope, and the new config also
  removes those references
- **THEN** the import succeeds without a foreign-key violation, because the
  referencing rows are replaced or removed before the standard row is deleted

#### Scenario: Parent organisation ordering holds

- **WHEN** a sync imports a company and its department in one config
- **THEN** the parent company row is upserted before the department that references
  it, and on removal the department is deleted before the parent

### Requirement: Forward-only hand-written migrations with checksum tracking

The system SHALL define versioned, forward-only schema migrations as hand-written
SQL files named with a fixed-width zero-padded ordinal and slug (`NNN_slug.sql`)
and applied in numeric-ordinal order by a runner. The recorded `version` of each
migration SHALL be the migration file name WITHOUT its extension - the `NNN_slug`
stem (for example, file `001_initial_schema.sql` is recorded as version
`001_initial_schema`), never the name with the `.sql` extension. As the first
step of the apply path (`ApplyPendingAsync`), and only there, the runner SHALL
ensure the `schema_migrations` tracking table exists via an idempotent bootstrap
step (`CREATE TABLE IF NOT EXISTS`) that is separate from the versioned domain
migrations, so a completely empty database can be migrated from scratch. The
bootstrap SHALL NOT run in the read-only state report, so reading migration state
never creates the table. The runner SHALL record each applied migration's
version, content checksum, and applied-at timestamp in `schema_migrations`.
The runner SHALL skip a migration whose recorded checksum matches, and SHALL fail
without applying further migrations if a recorded migration's checksum differs
from the checked-in file (a forward-only violation).

The runner SHALL NOT claim transactional atomicity for DDL migrations: on MySQL,
DDL causes an implicit commit, so a multi-statement DDL migration that fails
partway cannot be rolled back as one unit. The real guarantee is that a
migration's statements run and only after they all succeed is its
`(version, checksum, applied_at)` row recorded; on partial failure the runner
SHALL fail loudly, SHALL NOT record the version, and the migration SHALL remain
re-attemptable on a later run. Migrations SHALL be authored to be safe to re-run
where practical (e.g. `CREATE TABLE IF NOT EXISTS`). Transactions SHALL be used
only where they help on MySQL (the version-record insert and any DML).

Migrations SHALL be applied explicitly (an operator-run command), not implicitly
at web-app startup. The store SHALL assume the schema is current and SHALL NOT run
DDL on its own.

#### Scenario: Fresh empty database migrates from scratch

- **WHEN** migrations are applied to a completely empty database that has no
  tables at all (not even `schema_migrations`)
- **THEN** the runner bootstraps the migrations table, applies the migrations, and
  the four entity tables (standards, controls, organisations, scopes), the
  `control` `maps_to` relation table, and the `schema_migrations` table exist with
  their primary keys, foreign keys (including the organisation self-FK and the
  scope organisation/standard FKs), indexes, and binary-collation identifier columns

#### Scenario: Migrations apply in numeric-ordinal order

- **WHEN** the runner enumerates migration files named `001_...`, `002_...`, and
  `010_...`
- **THEN** they are applied in numeric-ordinal order (`001`, `002`, `010`), not a
  string order that would misplace `010`

#### Scenario: Failed migration is not recorded and is re-attemptable

- **WHEN** a migration's statements fail partway through
- **THEN** the runner fails loudly, does not record that migration's version in
  `schema_migrations`, and a later run re-attempts the same migration

#### Scenario: Already-applied migration is skipped

- **WHEN** migrations are applied to a database whose `schema_migrations`
  already records a migration with a matching checksum
- **THEN** that migration is not re-applied

#### Scenario: Edited applied migration fails the runner

- **WHEN** a migration recorded as applied has a checksum that no longer matches
  its checked-in file
- **THEN** the runner fails with a clear message and applies no further migrations

#### Scenario: Applied migration missing from embedded migrations fails the runner

- **WHEN** `schema_migrations` records an applied `version` whose SQL file
  was deleted or renamed, so no embedded migration stem matches that version
- **THEN** the runner fails with a clear message, applies no migrations, and does
  not treat the renamed or deleted migration as a new pending migration

#### Scenario: Web app does not auto-migrate

- **WHEN** the web app starts against a database
- **THEN** it does not apply schema migrations as a side effect of startup

## ADDED Requirements

### Requirement: Scope disposition is unique per organisation per standard

The scopes table SHALL enforce a unique key on `(organisation_id, standard_id)` so
at most one disposition exists per organisation node per standard. A `disposition`
column SHALL store the enum value (`In` or `Out`). The same organisation MAY hold
independent dispositions for different standards.

#### Scenario: Duplicate mapping violates the unique key

- **WHEN** a second scope row for an existing `(organisation_id, standard_id)` pair
  is written directly to the store
- **THEN** the database rejects it on the unique key

#### Scenario: Same organisation across standards is allowed

- **WHEN** an organisation has a scope for standard A and another for standard B
- **THEN** both rows persist, because the unique key is on the pair, not the
  organisation alone
