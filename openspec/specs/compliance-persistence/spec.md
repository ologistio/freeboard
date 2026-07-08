# compliance-persistence Specification

## Purpose
TBD - created by archiving change add-gitops-mysql-persistence. Update Purpose after archive.
## Requirements
### Requirement: MySQL-backed store for the compliance domain

The system SHALL provide a MySQL-backed store that persists the compliance domain
(`Standard`, `Control`, `Requirement`, `Organisation`, `Scope`) and their
cross-references. The store SHALL live in a dedicated MIT project
(`Freeboard.Persistence`) that holds the MySQL client dependency, and SHALL NOT add
any database or socket dependency to `Freeboard.Core`. The store is the general
compliance data layer; GitOps `sync` is one writer into it (see the GitOps importer
requirement) and is not part of the store's identity. Each resource SHALL be stored
keyed on its immutable `id`; `title` is a mutable column and SHALL NOT be part of
any key or match. Each resource row SHALL also persist its `api_version`, a
`created_at` set on first insert, and an `updated_at` set on every write. A
`Standard` row SHALL also persist its `version`, `authority`, optional
`publisher`, and optional `source_url` metadata (null when unset or for
pre-migration rows).

#### Scenario: Domain persists keyed on id

- **WHEN** a validated config is written to the store
- **THEN** each `Standard`, `Control`, `Requirement`, `Organisation`, and `Scope`
  is stored as a row whose primary key is its `id`, with `title`, `api_version`,
  `created_at`, and `updated_at` columns

#### Scenario: Store dependency stays out of Core

- **WHEN** the persistence project and the MySQL client are added
- **THEN** the `Freeboard.Core` assembly still references no
  `System.Net.Http` or `System.Net.Sockets` types, and `Freeboard.Core` gains no
  reference to the persistence project or any database client

### Requirement: Identifier columns use binary collation

Every `id` and foreign-key column in the schema SHALL use a binary collation
(utf8mb4_bin) so that identifier comparison is case-sensitive and exact-byte,
matching the ordinal identity semantics of `Freeboard.Core` validation. The
schema SHALL NOT rely on a case-insensitive default collation for identifier
columns.

#### Scenario: Case-distinct ids remain distinct

- **WHEN** two resources of the same kind have ids that differ only in case
  (for example `ctrl-a` and `CTRL-A`) and both are written
- **THEN** the store holds two distinct rows, because the id column collation is
  binary and does not treat them as equal

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

### Requirement: Identity and upsert key on id, never title

The store's write path SHALL match existing rows by `id` only. Writing a config
whose resource has the same `id` but a changed `title` SHALL update the existing
row's `title`, not create a new row. The write path SHALL NEVER match, dedupe, or
key on `title`.

#### Scenario: Changed title updates the same row

- **WHEN** a resource is written, then written again with the same `id` and a
  different `title`
- **THEN** the store holds one row for that `id` with the updated `title`

#### Scenario: Match resolves on id only

- **WHEN** a write keys on `id`
- **THEN** identity resolution uses `id` only and no match is made on `title`

#### Scenario: Changed api_version updates the same row

- **WHEN** a resource is written, then written again with the same `id` and a
  different `api_version`
- **THEN** the store holds one row for that `id` with the updated `api_version`,
  not a new row

#### Scenario: Re-sync preserves created_at and advances updated_at

- **WHEN** an existing resource is written again (same `id`)
- **THEN** its `created_at` is unchanged and its `updated_at` is advanced to the
  time of the new write

### Requirement: General read store and GitOps importer abstractions

The system SHALL expose separate abstractions for reading the store and for
importing config into it. `IComplianceStore` (the general read abstraction, in the
`Freeboard.Persistence` namespace) SHALL provide read methods returning the
persisted standards (with their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata), controls (with their resolved `maps_to`
`Requirement` ids, read from the `control_requirements` join), requirements (with
their resolved owning `standard`, `theme`, `statement`, `guidance`,
`citation_label`, and `citation_url`), organisations (with resolved `parent`),
scopes (with resolved `organisation`, `standard`, and `disposition`), and
requirement-scopes (with resolved `organisation`, `requirement`, and `disposition`)
and per-kind counts that include requirements and requirement-scopes. `IGitOpsImporter`
(in the `Freeboard.Persistence.GitOps` namespace - GitOps is one writer into the
general store) SHALL provide a method that replaces the persisted set from an
already-validated `GitOpsConfig`. `IGitOpsImporter.ImportAsync` SHALL document that
its caller guarantees the config has been validated; the importer SHALL NOT re-run
Core validation. The web app's dependency-injection registration SHALL register
`IComplianceStore` for reads, so the web app's service provider does not resolve
`IGitOpsImporter` or `IMigrationRunner`. The MySQL implementations SHALL satisfy
these abstractions. Consumers SHALL depend on the abstractions, not the concrete
implementations.

#### Scenario: Read returns persisted domain

- **WHEN** a caller invokes the `IComplianceStore` read methods after an import
- **THEN** it receives the persisted standards (with metadata), controls,
  requirements, organisations, scopes, and requirement-scopes with their `id`,
  `title`, and resolved references

#### Scenario: Counts include requirements and requirement-scopes

- **WHEN** a caller reads the per-kind counts after an import
- **THEN** the counts include the number of persisted requirements and
  requirement-scopes alongside standards, controls, organisations, and scopes

#### Scenario: Import replaces the persisted set

- **WHEN** a caller imports an already-validated `GitOpsConfig` via `IGitOpsImporter`
- **THEN** the store reflects exactly that config: resources present by `id` are
  upserted, and resources whose `id` is no longer present are removed

#### Scenario: Web registration resolves only the reader

- **WHEN** the web app's service provider is built from its read-path DI registration
- **THEN** it resolves `IComplianceStore` and does NOT resolve `IGitOpsImporter`
  or `IMigrationRunner`

### Requirement: Import order is FK-safe and replaces the whole persisted set

The importer SHALL run in a fixed order within one DML transaction: upsert domain
rows by `id` in FK-safe order (standards, including their metadata; then
requirements, whose rows reference standards; then controls; then organisations
parent-before-child); then prune absent scopes and upsert the new scope set (a
scope references organisations and standards, and the prune precedes the upsert so a
scope whose `id` is renamed while keeping its `(organisation, standard)` pair does
not collide on the unique key); then prune absent requirement-scopes and upsert the
new requirement-scope set (a requirement-scope references organisations and
requirements, both already upserted, and the prune precedes the upsert so a
requirement-scope whose `id` is renamed while keeping its `(organisation,
requirement)` pair does not collide on the unique key); then replace all `maps_to`
cross-ref join rows for the imported set in the `control_requirements` join (delete
the existing join rows and insert the rows derived from the new config, a whole-set
replacement rather than a per-parent diff), which is safe because controls and
requirements are both upserted by now; then delete remaining domain rows whose `id`
is absent from the config in FK-safe order (requirements before standards; then
organisations child-before-parent; then controls; then standards). The absent
scope and requirement-scope prunes precede the absent organisation, standard, and
requirement deletes, so a removed organisation or requirement no longer has a
referencing scope or requirement-scope row when it is deleted. Because a validated
config is acyclic and has no dangling references (a `Freeboard.Core` invariant), a
stable order exists and foreign-key constraints hold at commit.

#### Scenario: Dropping a referenced standard in the same sync succeeds

- **WHEN** a sync removes a Standard that, in the prior persisted state, was
  referenced by a Requirement via `standard` or by a Scope, and the new config also
  removes those references
- **THEN** the import succeeds without a foreign-key violation, because the
  referencing rows are replaced or removed before the standard row is deleted

#### Scenario: Dropping a referenced requirement in the same sync succeeds

- **WHEN** a sync removes a Requirement that, in the prior persisted state, was
  referenced by a RequirementScope, and the new config also removes that
  requirement-scope
- **THEN** the import succeeds without a foreign-key violation, because the absent
  requirement-scope is pruned before the requirement row is deleted

#### Scenario: Renaming a scope that keeps its organisation and standard pair

- **WHEN** a sync renames a Scope's `id` while keeping the same
  `(organisation, standard)` pair
- **THEN** the import succeeds and the store holds the scope under its new `id`,
  because absent scopes are pruned before the scope upsert so the unique
  `(organisation, standard)` key is free for the new row

#### Scenario: Renaming a requirement-scope that keeps its organisation and requirement pair

- **WHEN** a sync renames a RequirementScope's `id` while keeping the same
  `(organisation, requirement)` pair
- **THEN** the import succeeds and the store holds the requirement-scope under its
  new `id`, because absent requirement-scopes are pruned before the upsert so the
  unique `(organisation, requirement)` key is free for the new row

#### Scenario: Requirement upserted after its standard

- **WHEN** a sync imports a standard and a requirement that references it in one
  config
- **THEN** the standard row is upserted before the requirement that references it,
  and on removal the requirement is deleted before the standard

#### Scenario: Parent organisation ordering holds

- **WHEN** a sync imports a company and its department in one config
- **THEN** the parent company row is upserted before the department that references
  it, and on removal the department is deleted before the parent

### Requirement: Migration runner applies pending migrations and reports state

The system SHALL provide a migration runner, `IMigrationRunner`, in the
`Freeboard.Persistence.System` namespace (migrations are a system/platform
concern, not a gitops artifact). The runner SHALL both apply pending
migrations and report migration state. The state report SHALL classify every
embedded migration version present in `schema_migrations` as current and the rest
as pending. The `sync` migrate-first gate depends on this report to decide whether
the schema is current.

The state report SHALL be strictly side-effect-free: it SHALL perform no DDL and
no writes, and SHALL NOT create the `schema_migrations` table. On a fresh database
where `schema_migrations` does not exist, the state report SHALL classify every
embedded migration as pending (none current) and SHALL report no integrity
violation (there are zero recorded versions), WITHOUT creating the table or
otherwise mutating the database. Where `schema_migrations` exists, the state
report SHALL read it and classify current vs pending. The bootstrap that creates
`schema_migrations` SHALL live only in the apply path, not in the state report
(see the forward-only migrations requirement).

The state report SHALL ALSO surface forward-only integrity violations, by pure
reads only, so a corrupt schema cannot be classified as "current" and bypass the
migrate-first gate. By comparing the recorded `(version, checksum)` rows against
the embedded migration set - the same comparison the apply path runs - the state
report SHALL report an integrity violation when (a) an applied migration's
checksum no longer matches its embedded file, or (b) a recorded applied `version`
has no embedded stem (deleted or renamed). Reporting these violations SHALL remain
strictly read-only (a comparison of already-read rows; no DDL, no writes). The
apply path SHALL keep its own fail-loud check and SHALL still refuse to apply over
a corrupt or missing-migration schema.

#### Scenario: State report surfaces a checksum mismatch without writing

- **WHEN** the runner reports state against a database whose `schema_migrations`
  records an applied migration with a checksum that no longer matches its embedded
  file
- **THEN** the returned state reports an integrity violation, and the report
  performs no DDL and no writes

#### Scenario: State report surfaces a recorded-but-missing applied migration without writing

- **WHEN** the runner reports state against a database whose `schema_migrations`
  records an applied `version` with no matching embedded migration stem
- **THEN** the returned state reports an integrity violation, and the report
  performs no DDL and no writes

The runner SHALL fail loudly (an operational failure) if `schema_migrations`
records an applied `version` that is NOT present among the embedded migration
stems - an applied migration whose SQL file was later deleted or renamed. This
check runs before any pending migration is applied and preserves forward-only
integrity: because the version key is the file stem, a slug rename would otherwise
silently orphan the old applied row and re-present the renamed file as a new
pending migration, bypassing forward-only protection. This is a distinct failure
from the checksum mismatch of a still-present migration.

#### Scenario: Reports pending when a migration is unapplied

- **WHEN** the runner reports state against a database that records some but not
  all embedded migration versions
- **THEN** the recorded versions are reported as current and the remaining
  embedded versions are reported as pending

#### Scenario: State report on an empty database creates nothing

- **WHEN** the runner reports state against a completely empty database that has
  no `schema_migrations` table
- **THEN** every embedded migration is reported as pending, none current, with no
  recorded-but-missing-migration violation, and the database is left unchanged -
  no `schema_migrations` table and no other table is created

#### Scenario: Runner fails when an applied migration is missing from embedded migrations

- **WHEN** `schema_migrations` records an applied `version` whose migration
  file is no longer present among the embedded migrations (deleted or renamed)
- **THEN** the runner fails loudly with a clear message, applies no migrations,
  and does not treat the renamed or deleted migration as a new pending migration

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

### Requirement: Import runs in a transaction and writes nothing on invalid config

The import write path SHALL run within a single DML transaction so a failed write
does not leave the store partially updated. Validation is the caller's
responsibility: the importer accepts an already-validated config as a documented
precondition and does not re-validate. The caller (the `sync` command) SHALL
validate the config (via the `Freeboard.Core` loader and validator) before
importing, and on any validation error SHALL NOT call the importer, so the store
is not written. The "invalid config writes nothing" guarantee is asserted at the
sync/CLI level. The importer SHALL hard-remove rows whose `id` is absent from the
imported config in this increment; soft-delete on removal is a forward principle
for the later real-apply change and is not built here.

#### Scenario: Invalid config does not reach the store

- **WHEN** a caller attempts to import a config that fails validation
- **THEN** the store is not modified

#### Scenario: Failed import does not partially apply

- **WHEN** an import fails partway through
- **THEN** the store reflects its prior state, not a partial update

### Requirement: Connection strings are supplied out of band

The store's MySQL connection string SHALL be supplied via environment variable,
.NET user-secrets, or a configuration provider. It SHALL NOT be read from the
GitOps YAML config and SHALL NOT be committed to the repository.

#### Scenario: Connection string is not in YAML

- **WHEN** the store is configured
- **THEN** the connection string comes from environment, user-secrets, or a
  config provider, and never from the GitOps YAML config or a committed file

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

### Requirement: Requirements and standard metadata persistence

The store SHALL persist the `Requirement` kind in a dedicated `requirements`
table, created by migration `008`. Each requirement row SHALL be keyed on its
immutable `id` and SHALL hold `api_version`, `title`, a `standard_id` foreign key
to the owning standard, `theme`, `statement`, a nullable `guidance`,
`citation_label`, `citation_url`, a `created_at` set on first insert, and an
`updated_at` set on every write. The `id` and `standard_id` columns SHALL use
binary collation (`utf8mb4_bin`) so requirement identity is exact-byte, consistent
with `Freeboard.Core`. The `standard_id` foreign key SHALL reference
`standards(id)` with `ON DELETE RESTRICT`, so a standard cannot be deleted while a
requirement still references it; the importer removes referencing requirements
first (see the import-order requirement).

Migration `008` SHALL also add nullable `version`, `authority`, `publisher`, and
`source_url` columns to the existing `standards` table. Adding those columns SHALL
be additive and forward-only: it SHALL NOT rewrite or drop existing `standards`
rows, and pre-migration rows SHALL read back with null metadata until they are
re-synced. The columns are nullable at the storage layer even though `version` and
`authority` are required at the config-validation layer, so pre-migration rows
survive without fabricated data.

Migration `008` SHALL also repoint the control cross-reference: it SHALL DROP the
`control_standards` join table and CREATE a `control_requirements` join table with
columns `control_id` and `requirement_id`, a composite primary key over both, a
foreign key from `requirement_id` to `requirements(id)`, and a foreign key from
`control_id` to `controls(id)`. Both join columns SHALL use binary collation
(`utf8mb4_bin`) and the FK on-delete behaviour SHALL match the join semantics of the
dropped `control_standards` table (cascade on delete). Pre-1.0 and forward-only: no
`control_standards` rows are migrated; the join is rebuilt on the next import.

#### Scenario: Fresh database has control_requirements and no control_standards

- **WHEN** migrations are applied to a fresh database through `008`
- **THEN** the `control_requirements` join table exists with its composite primary
  key, foreign keys to `controls` and `requirements`, and binary-collation join
  columns, and the `control_standards` table does not exist

#### Scenario: Requirement persists keyed on id with a standard foreign key

- **WHEN** a validated config with a `Requirement` owned by a `Standard` is
  imported
- **THEN** a `requirements` row exists keyed on the requirement `id`, holding its
  `title`, `theme`, `statement`, `guidance` (or null), `citation_label`,
  `citation_url`, `api_version`, `created_at`, `updated_at`, and a `standard_id`
  foreign key to the owning standard

#### Scenario: Fresh database gains the requirements table and standards metadata columns

- **WHEN** migrations are applied to a fresh database through `008`
- **THEN** the `requirements` table exists with its primary key, `standard_id`
  foreign key to `standards`, index on `standard_id`, and binary-collation `id`
  and `standard_id` columns, and the `standards` table has nullable `version`,
  `authority`, `publisher`, and `source_url` columns

#### Scenario: Case-distinct requirement ids remain distinct

- **WHEN** two requirements have ids that differ only in case and both are written
- **THEN** the store holds two distinct rows, because the `id` column collation is
  binary

### Requirement: Requirement-scope persistence

The store SHALL persist the `RequirementScope` kind in a dedicated
`requirement_scopes` table, created by migration `009`. Each requirement-scope row
SHALL be keyed on its immutable `id` and SHALL hold `api_version`, `title`, an
`organisation_id` foreign key to the referenced organisation, a `requirement_id`
foreign key to the referenced requirement, a `disposition` column storing the enum
value (`In` or `Out`), a `created_at` set on first insert, and an `updated_at` set on
every write. The `id`, `organisation_id`, and `requirement_id` columns SHALL use
binary collation (`utf8mb4_bin`) so identity is exact-byte, consistent with
`Freeboard.Core`. The table SHALL enforce a unique key on
`(organisation_id, requirement_id)` so at most one disposition exists per
organisation node per requirement; because a requirement determines its standard,
this is equivalent to uniqueness per `(organisation, standard, requirement)`. The
`organisation_id` foreign key SHALL reference `organisations(id)` and the
`requirement_id` foreign key SHALL reference `requirements(id)`, both with
`ON DELETE RESTRICT`, matching the `scopes` table: the importer removes referencing
requirement-scopes before deleting an organisation or requirement (see the
import-order requirement). Migration `009` SHALL be additive and forward-only: it
SHALL create only the `requirement_scopes` table and SHALL NOT alter, rewrite, or
drop any existing table.

#### Scenario: Fresh database gains the requirement_scopes table

- **WHEN** migrations are applied to a fresh database through `009`
- **THEN** the `requirement_scopes` table exists with its primary key, the unique
  key on `(organisation_id, requirement_id)`, the index on `requirement_id`, foreign
  keys to `organisations` and `requirements`, and binary-collation `id`,
  `organisation_id`, and `requirement_id` columns

#### Scenario: RequirementScope persists keyed on id with organisation and requirement foreign keys

- **WHEN** a validated config with a `RequirementScope` mapping an organisation to a
  requirement is imported
- **THEN** a `requirement_scopes` row exists keyed on the requirement-scope `id`,
  holding its `title`, `api_version`, `created_at`, `updated_at`, a `disposition`
  column, an `organisation_id` foreign key, and a `requirement_id` foreign key

#### Scenario: Duplicate mapping violates the unique key

- **WHEN** a second requirement-scope row for an existing
  `(organisation_id, requirement_id)` pair is written directly to the store
- **THEN** the database rejects it on the unique key

#### Scenario: Case-distinct requirement-scope ids remain distinct

- **WHEN** two requirement-scopes have ids that differ only in case and both are
  written
- **THEN** the store holds two distinct rows, because the `id` column collation is
  binary

### Requirement: Vendor and VendorScope persistence

The system SHALL persist the `Vendor` and `VendorScope` kinds in dedicated MySQL
tables `vendors` and `vendor_scopes`, created by a forward-only migration that
alters no existing table. Ids and foreign-key columns SHALL use `utf8mb4_bin` to
match Core's exact-byte id identity, consistent with the existing compliance
tables. The `vendors` table SHALL hold `id`, `api_version`, `title`, `created_at`,
and `updated_at`. The `vendor_scopes` table SHALL hold `id`, `api_version`,
`title`, a `vendor_id` foreign key to `vendors`, a nullable `requirement_id`
foreign key to `requirements`, a nullable `control_id` foreign key to `controls`, a
`disposition`, a nullable `justification`, `created_at`, and `updated_at`. Exactly
one of `requirement_id` or `control_id` SHALL be set on each row. This invariant
SHALL be enforced primarily by the Core validator before import (the user-facing
diagnostic) and additionally by a table `CHECK` constraint on `vendor_scopes` that
rejects a row with both target columns set or both null, as a database-level
backstop. The foreign keys SHALL be
`ON DELETE RESTRICT`, matching the existing scope tables, so the importer prunes
referencing vendor-scopes before deleting a vendor, requirement, or control. The
table SHALL enforce at most one row per `(vendor_id, requirement_id)` and at most
one per `(vendor_id, control_id)` with unique keys.

The GitOps importer SHALL sync vendors and vendor-scopes in the same
whole-set-replace transaction as the other kinds, in a foreign-key-safe order:
vendors upserted with the other independent rows; vendor-scopes replaced as a whole
set (delete-all then insert), like requirement-scopes; absent vendors deleted after
their referencing vendor-scopes are gone. A blank `justification` SHALL be stored as
NULL.

The read store SHALL expose the persisted vendors and vendor-scopes through the
`IComplianceStore` abstraction, and the persisted-counts read SHALL include the
vendor and vendor-scope counts.

#### Scenario: Vendors and vendor-scopes round-trip through import and read

- **WHEN** a valid config containing vendors and vendor-scopes is imported and then
  read back through the store
- **THEN** every vendor and vendor-scope is persisted and returned with its `id`,
  `title`, references, `disposition`, and `justification` (null when absent)

#### Scenario: Out vendor-scope keeps its justification

- **WHEN** a vendor-scope with `disposition: Out` and a non-empty `justification` is
  imported and read back
- **THEN** the stored row returns that justification text

#### Scenario: Import order respects foreign keys when a vendor is removed

- **WHEN** an import removes a vendor that still has vendor-scopes in the previous
  persisted set
- **THEN** the importer deletes the referencing vendor-scopes before deleting the
  vendor, so the restrict foreign key is not violated

#### Scenario: Import order respects foreign keys when a targeted requirement is removed

- **WHEN** an import removes a requirement that a vendor-scope in the previous
  persisted set targets
- **THEN** the importer replaces the vendor-scope set (dropping the referencing
  vendor-scope) before deleting the requirement, so the requirement RESTRICT
  foreign key is not violated

#### Scenario: Import order respects foreign keys when a targeted control is removed

- **WHEN** an import removes a control that a vendor-scope in the previous persisted
  set targets
- **THEN** the importer replaces the vendor-scope set (dropping the referencing
  vendor-scope) before deleting the control, so the control RESTRICT foreign key is
  not violated

#### Scenario: Renamed vendor-scope keeping the same pair survives resync

- **WHEN** an import re-syncs a vendor-scope whose `id` changed while its
  `(vendor, target)` pair stayed the same
- **THEN** the whole-set replace drops the old row and inserts the new one, so the
  `(vendor, requirement)` / `(vendor, control)` unique key is not violated and the
  pair persists under the new id

#### Scenario: Vendor-scope pair-swap keeping ids survives resync

- **WHEN** an import re-syncs two vendor-scopes that swap their `(vendor, target)`
  pairs while keeping their existing `id`s - row A takes row B's pair and row B takes
  row A's pair
- **THEN** the whole-set replace deletes both old rows before inserting the swapped
  pairs, so neither `(vendor, requirement)` / `(vendor, control)` unique key is
  transiently violated - an id-keyed upsert could not do this, because both ids
  persist while their pairs cross, which is why the importer full-replaces
  `vendor_scopes` rather than upserting them

#### Scenario: Counts include vendors and vendor-scopes

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include the number of persisted vendors and vendor-scopes

#### Scenario: Database rejects a vendor-scope with no single target

- **WHEN** a `vendor_scopes` row is written directly with both `requirement_id` and
  `control_id` set, or with both null
- **THEN** the table `CHECK` constraint rejects the row, independently of the Core
  validator

### Requirement: EvidenceCollector persistence and Control evaluation column

The system SHALL persist the `EvidenceCollector` kind in a dedicated MySQL table
`evidence_collectors` and SHALL persist a control's `evaluation` rule in a new
nullable `evaluation` column on the existing `controls` table, both created by a
forward-only migration. The `controls.evaluation` column SHALL be a nullable
`VARCHAR(16)`; existing control rows read back `null` when no rule is set. Ids and
foreign-key columns SHALL use `utf8mb4_bin` to match Core's exact-byte id identity,
consistent with the existing compliance tables.

The `evidence_collectors` table SHALL hold `id`, `api_version`, `title`, a non-null
`control_id` foreign key to `controls`, a nullable `vendor_id` foreign key to
`vendors`, a `type`, a `frequency`, a nullable `threshold` integer, a nullable
`config` JSON value holding the type-specific settings map, `created_at`, and
`updated_at`. The two foreign keys SHALL be `ON DELETE RESTRICT`, matching the scope
tables, so the importer prunes referencing collectors before deleting a control or a
vendor. Identity SHALL be keyed on `id` only; the table SHALL NOT impose a secondary
uniqueness key, because a control MAY have several collectors.

The GitOps importer SHALL sync controls (with their evaluation rule) and
evidence-collectors in the same whole-set-replace transaction as the other kinds, in
a foreign-key-safe order: controls upserted by id with their `evaluation` column;
evidence-collectors upserted by id after controls and vendors are upserted (its
foreign keys point at both); absent evidence-collectors deleted before absent
vendors, controls, and requirements are deleted, so no RESTRICT foreign key is
violated. A blank `evaluation` SHALL be stored as NULL, a blank `threshold` as NULL,
and an empty `config` map as NULL; a non-empty `config` map SHALL be stored as a JSON
object.

The read store SHALL expose the persisted evidence-collectors through the
`IComplianceStore` abstraction, SHALL include the control's `evaluation` rule (null
when unset) on the control read, and the persisted-counts read SHALL include the
evidence-collector count.

#### Scenario: Evidence-collectors round-trip through import and read

- **WHEN** a valid config containing controls with an evaluation rule and
  evidence-collectors is imported and then read back through the store
- **THEN** every collector is persisted and returned with its `id`, `title`,
  `control`, `vendor` (null when absent), `type`, `frequency`, `threshold` (null when
  absent), and `config` map, and every control returns its `evaluation` rule (null
  when unset)

#### Scenario: Import order respects foreign keys when a targeted control is removed

- **WHEN** an import removes a control that an evidence-collector in the previous
  persisted set attaches to
- **THEN** the importer deletes the referencing collector before deleting the
  control, so the control RESTRICT foreign key is not violated

#### Scenario: Import order respects foreign keys when a named vendor is removed

- **WHEN** an import removes a vendor that an evidence-collector in the previous
  persisted set names
- **THEN** the importer deletes the referencing collector before deleting the vendor,
  so the vendor RESTRICT foreign key is not violated

#### Scenario: Evaluation rule is added to an existing control without data loss

- **WHEN** a control that previously had no `evaluation` is re-synced with an
  `evaluation` rule
- **THEN** the stored control row returns the new rule and its other columns and
  cross-references are unchanged

#### Scenario: Counts include evidence-collectors

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include the number of persisted evidence-collectors

### Requirement: AttestationTemplate persistence

The system SHALL persist the `AttestationTemplate` kind in a dedicated MySQL table
`attestation_templates`, created by a forward-only migration that alters no existing
table. Ids and foreign-key columns SHALL use `utf8mb4_bin` to match Core's exact-byte
id identity, consistent with the existing compliance tables.

The `attestation_templates` table SHALL hold `id`, `api_version`, `title`, a non-null
`control_id` foreign key to `controls`, a `type`, a nullable `body` text value, a
nullable `fields` JSON value holding the ordered list of form fields, a nullable
`pass_mark` integer, a nullable `quiz` JSON value holding the ordered list of quiz
items, `created_at`, and `updated_at`. The `control_id` foreign key SHALL be
`ON DELETE RESTRICT`, matching the other reference tables, so the importer prunes
referencing templates before deleting a control. Identity SHALL be keyed on `id`
only; the table SHALL NOT impose a secondary uniqueness key, because a control MAY
have several attestation templates.

The GitOps importer SHALL sync attestation-templates in the same whole-set-replace
transaction as the other kinds, in a foreign-key-safe order: attestation-templates
upserted by id after controls are upserted (its foreign key points at controls);
absent attestation-templates deleted before absent controls are deleted, so the
`control_id` RESTRICT foreign key is not violated. A blank `body` SHALL be stored as
NULL, a blank `pass_mark` as NULL, and an empty `fields` or `quiz` list as NULL; a
non-empty `fields` or `quiz` list SHALL be stored as a JSON array. Each stored quiz
item SHALL retain its `answer` in the `quiz` JSON, because the later grading runtime
needs it; the answer lives only in storage and is never surfaced by the read store.

The read store SHALL expose the persisted attestation-templates through the
`IComplianceStore` abstraction, deserializing the `fields` and `quiz` JSON back into
their typed lists, and the persisted-counts read SHALL include the
attestation-template count. The read model's quiz items SHALL NOT carry the `answer`:
the read store projects each quiz item to an answer-free shape at the store boundary,
so no read surface (API, CLI, or web register) can expose a training quiz's correct
answer.

#### Scenario: Read store redacts the quiz answer

- **WHEN** a training template with a quiz `answer` is imported and then read back
  through the store
- **THEN** the returned quiz items expose their `prompt` and `options` but carry no
  `answer`, while the stored `quiz` JSON still contains the answer for later grading

#### Scenario: Attestation-templates round-trip through import and read

- **WHEN** a valid config containing a manual template and a training template is
  imported and then read back through the store
- **THEN** every template is persisted and returned with its `id`, `title`,
  `control`, `type`, `body` (null when absent), `fields` (empty when absent),
  `pass_mark` (null when absent), and `quiz` (empty when absent)

#### Scenario: Import order respects foreign keys when a targeted control is removed

- **WHEN** an import removes a control that an attestation-template in the previous
  persisted set attaches to
- **THEN** the importer deletes the referencing template before deleting the control,
  so the control RESTRICT foreign key is not violated

#### Scenario: Counts include attestation-templates

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include the number of persisted attestation-templates

### Requirement: Runtime Evidence persistence

The system SHALL persist runtime Evidence in two dedicated MySQL tables, `evidence_runs`
and `evidence_run_checks`, created by a forward-only migration (`014`) that alters no
existing table. One collector run SHALL be one immutable `evidence_runs` row with its nested
`evidence_run_checks` rows; Evidence is append-only and SHALL NOT be updated after insert.

The `evidence_runs` table SHALL NOT hold a foreign key to any GitOps-managed config table
(`evidence_collectors`, `controls`, `vendors`). Compliance evidence history MUST survive a
GitOps sync that hard-deletes a pruned collector, control, or vendor, so collector identity
SHALL be snapshotted onto each run at ingest time as plain values, not referenced. The
`evidence_runs` table SHALL hold: an `id` (the existing ULID `CHAR(26)` form used for
sessions and users); a `collector_id` plain string (no FK) using `utf8mb4_bin` to match
Core's exact-byte id identity; snapshot columns `collector_title`, `control_id` (no FK,
`utf8mb4_bin`, NOT NULL because the source `evidence_collectors.control_id` is NOT NULL so an
ingest always has a control id to snapshot), `vendor_id` (no FK, `utf8mb4_bin`, nullable
because its source `evidence_collectors.vendor_id` is nullable), and `collector_type` copied
from the collector's `evidence_collectors` register row at ingest; a caller-supplied
`run_id`; a `schema_version`; an optional `collector_version`; a `started_at`; a
`finished_at`; a server-set `received_at`; a `request_body_sha256` (`BINARY(32)`) holding
the SHA-256 of the exact request body; the raw derived summary counts `hard_fail_count`,
`soft_fail_count`, and `total_count`; and an optional `metadata` JSON value.

The store SHALL NOT compute or persist a control-level or rollup compliance verdict at
ingest: `hard_fail_count`, `soft_fail_count`, and `total_count` are raw derived counts, not a
status. Categorical rollup remains the scoring engine's responsibility. `hard_fail_count`
SHALL count checks with `severity = hard` and `status = fail`; `soft_fail_count` SHALL count
checks with `severity = soft` and `status = fail`; `total_count` SHALL be the number of
checks in the run.

Each `evidence_run_checks` row SHALL hold: an `id`; an `evidence_run_id` foreign key to
`evidence_runs` with `ON DELETE CASCADE`; a `name` (bounded length, unique within a run); a
`severity` (`hard`/`soft`); a `status` (`pass`/`fail`/`unknown`/`not_applicable`); an
optional `detail`; an optional `data` JSON value; and a `seq` preserving check order. The
Evidence write store SHALL live in `Freeboard.Persistence` and add no database dependency to
`Freeboard.Core`.

#### Scenario: Fresh database gains the evidence tables

- **WHEN** migrations are applied to a fresh database through `014`
- **THEN** the `evidence_runs` table exists with its primary key, the idempotency unique key
  `(collector_id, run_id)`, its snapshot columns, and its summary count columns, and holds no
  foreign key to `evidence_collectors`, `controls`, or `vendors`; and the
  `evidence_run_checks` table exists with its primary key, `evidence_run_id` foreign key to
  `evidence_runs` with `ON DELETE CASCADE`, and its `severity`, `status`, `detail`, `data`,
  and `seq` columns

#### Scenario: A run persists as one evidence row with its checks and snapshot

- **WHEN** a collector run with several checks is written through the Evidence write store
- **THEN** one `evidence_runs` row is persisted with its `collector_id`, snapshot
  `collector_title`/`control_id`/`vendor_id`/`collector_type`, `run_id`, `started_at`,
  `finished_at`, `received_at`, `request_body_sha256`, and derived counts, and one
  `evidence_run_checks` row per check is persisted with its `name`, `severity`, `status`,
  `detail`, `data`, and `seq`

#### Scenario: Evidence history survives collector removal

- **WHEN** a GitOps sync hard-deletes a collector's `evidence_collectors` row after evidence
  runs for it exist
- **THEN** the `evidence_runs` rows for that collector remain, because they hold no foreign
  key to `evidence_collectors` and carry the collector identity as a snapshot

#### Scenario: Summary counts are raw counts, not a verdict

- **WHEN** a run includes checks of mixed severity and status
- **THEN** the persisted `hard_fail_count`, `soft_fail_count`, and `total_count` are the raw
  counts of matching checks and no control-level or rollup status is computed or stored at
  ingest

### Requirement: Evidence idempotency and immutability

The `evidence_runs` table SHALL enforce a unique key on `(collector_id, run_id)` so a
re-POST of the same run does not create a second row. The write store SHALL be append-only
within one DML transaction: it SHALL insert the `evidence_runs` row and all its
`evidence_run_checks` rows together, and on a duplicate `(collector_id, run_id)` it SHALL NOT
insert a second row or mutate the first. The store SHALL report, for a duplicate, whether the
stored `request_body_sha256` matches the incoming one, so the endpoint can distinguish a
safe replay from a conflicting body. A partial failure SHALL leave the store in its prior
state, not a half-written run.

The write result SHALL surface the persisted run's `received_at`, `hard_fail_count`,
`soft_fail_count`, and `total_count` alongside the Evidence id and the new-vs-duplicate and
body-matches flags, so the endpoint can build the response body without a second read. For a
new insert these are the values just written; for a same-body duplicate (a replay) these are
the ORIGINAL stored values of the existing run, read back within the same transaction, so a
replay response reports the original run's `received_at` and counts, not values derived from
the replayed request.

#### Scenario: Duplicate run with the same body is a no-op returning the existing id

- **WHEN** a run whose `(collector_id, run_id)` already exists is written again with an
  identical `request_body_sha256`
- **THEN** the store returns the existing Evidence id with the existing run's `received_at` and
  summary counts, reports the body as matching, and does not insert a second `evidence_runs` row
  or add or change any `evidence_run_checks` row

#### Scenario: Duplicate run with a different body is reported as a conflict

- **WHEN** a run whose `(collector_id, run_id)` already exists is written again with a
  different `request_body_sha256`
- **THEN** the store does not insert or mutate anything and reports the body as conflicting so
  the endpoint can reject it

#### Scenario: Failed write does not partially apply

- **WHEN** an Evidence write fails partway through inserting its checks
- **THEN** the store reflects its prior state and no `evidence_runs` row for that run remains

### Requirement: Per-collector credential persistence

The system SHALL persist per-collector machine credentials in a dedicated MySQL table
`collector_credentials`, created by the same forward-only migration, that alters no existing
table. Each row SHALL hold an `id`, a `collector_id` foreign key to `evidence_collectors`
with `ON DELETE CASCADE` (removing a collector revokes its credentials), a `token_hash`
(`BINARY(32)`, unique) holding the keyed HMAC-SHA256 of the credential secret, a
`token_key_version`, a `created_at`, a nullable `last_seen_at`, a nullable `expires_at`, and a
nullable `revoked_at`. The `collector_id` column SHALL use `utf8mb4_bin`. Unlike
`evidence_runs`, this table MAY foreign-key to `evidence_collectors`: a credential is live
config, not compliance history, so cascading its deletion with the collector is correct. The
store SHALL support looking a credential up by token hash (for authentication), issuing a
credential (insert), revoking one (set `revoked_at`), and a best-effort last-seen touch that
updates `last_seen_at` for a credential. The `last_seen_at` column SHALL be written on a
successful collector authentication (mirroring the session store's last-seen update), so the
column reflects real collector activity and is not dead schema; a failure of that update SHALL
NOT fail the authenticated request. The raw token SHALL never be stored.

#### Scenario: Fresh database gains the collector_credentials table

- **WHEN** migrations are applied to a fresh database through `014`
- **THEN** the `collector_credentials` table exists with its primary key, its unique
  `token_hash`, its `token_key_version`, its `collector_id` foreign key to
  `evidence_collectors` with `ON DELETE CASCADE`, and its nullable `last_seen_at`,
  `expires_at`, and `revoked_at` columns

#### Scenario: Lookup resolves a live credential by token hash

- **WHEN** the store looks up a credential by the keyed HMAC of a presented secret and the
  matching row has a null `revoked_at` and no elapsed `expires_at`
- **THEN** the store returns the credential with its `collector_id`, `token_key_version`,
  `expires_at`, and `revoked_at`

#### Scenario: Successful authentication touches last-seen

- **WHEN** a collector credential authenticates successfully and the store's last-seen touch is
  invoked for it
- **THEN** the credential's `last_seen_at` is updated to the authentication time, and a failure
  of that touch does not fail the authenticated request

#### Scenario: Deleting a collector removes its credentials

- **WHEN** a collector is removed and its `evidence_collectors` row is deleted
- **THEN** its `collector_credentials` rows are removed by the cascade

