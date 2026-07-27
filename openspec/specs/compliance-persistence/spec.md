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

The store SHALL persist `Control.maps_to` (Requirement ids) as relational rows in the
`control_requirements` join with foreign keys to the referenced requirement by `id`, not as
denormalized text. `Asset.parent` SHALL be persisted as a scalar reference column with NO
foreign key (a dangling-tolerated asset reference validated by the application), consistent
with the unified `assets` table from the asset unification. `Scope.subject` SHALL be
persisted as a scalar `subject_id` column with NO foreign key (a dangling-tolerated asset
reference validated by the application), and the one populated `Scope` target (`standard`,
`requirement`, or `control`) SHALL be persisted as a nullable foreign-key column referencing
`standards`, `requirements`, or `controls` by `id`. Referential integrity for the `maps_to`
and scope-target references SHALL be enforced by the database; the asset `parent` and the
scope subject are enforced by the application, not a foreign key. Reads SHALL return the
references resolved by `id`.

#### Scenario: maps_to stored as a relation

- **WHEN** a `Control` with two `maps_to` Requirement ids is written
- **THEN** two relation rows in the `control_requirements` join link the control id to
  each requirement id, each with a foreign key to the requirements table

#### Scenario: Asset parent stored as a scalar reference with no foreign key

- **WHEN** an `Asset` with a `parent` is written
- **THEN** its row holds a scalar `parent` column with no foreign key (a dangling parent
  is tolerated), and a root asset stores a null `parent`

#### Scenario: Scope stored with a scalar subject and a target foreign key

- **WHEN** a `Scope` mapping a subject to a standard, requirement, or control with a
  disposition is written
- **THEN** its row holds a `subject_id` column with no foreign key, exactly one populated
  target foreign-key column (`standard_id`, `requirement_id`, or `control_id`), and a
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

#### Scenario: A GitOps sync replaces a scope rather than upserting it

- **WHEN** an existing `Scope` is written again (same `id`) by a GitOps sync
- **THEN** the whole-set replace gives it a new `created_at`, so the preserved-`created_at`
  rule above does NOT hold for a sync-authored scope; the replace is required for target
  foreign-key safety and neither timestamp is read

#### Scenario: An app-managed scope write preserves created_at

- **WHEN** an existing `Scope` is updated through an app-managed disposition route
- **THEN** its `created_at` is unchanged and only `updated_at` advances, as for every
  other kind

### Requirement: General read store and GitOps importer abstractions

The system SHALL expose separate abstractions for reading the store and for
importing config into it. `IComplianceStore` (the general read abstraction, in the
`Freeboard.Persistence` namespace) SHALL provide read methods returning the
persisted standards (with their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata), controls (with their resolved `maps_to`
`Requirement` ids, read from the `control_requirements` join), requirements (with
their resolved owning `standard`, `theme`, `statement`, `guidance`,
`citation_label`, and `citation_url`), organisations (with resolved `parent`), and
scopes (each with its resolved `subject`, exactly one target of
`standard`/`requirement`/`control`, `disposition`, `justification`, and - carried by a
`LEFT JOIN` on the `assets` table keyed on `subject_id` for the web read's subject-readability
narrowing - the subject's resolved `type`, `source`, `state`, `parent`, and `owner`, which are
server-side narrowing inputs the API response does not expose) and per-kind
counts that include requirements and one unified scope count. `IGitOpsImporter`
(in the `Freeboard.Persistence.GitOps` namespace - GitOps is one writer into the
general store) SHALL provide a method that replaces the persisted set from an
already-validated `GitOpsConfig`. `IGitOpsImporter.ImportAsync` SHALL document that
its caller guarantees the config has been validated; the importer SHALL NOT re-run
Core validation. `ImportAsync` SHALL return an import result carrying any scope subjects
that do not resolve against the persisted `assets` table (the DB-accurate subject-resolution
predicate) as non-blocking warnings, since Core, having no database, cannot evaluate a
discovered or retired subject; the importer SHALL compute that set within the import
transaction before commit (rolling the import back on a check failure), not after commit.
The web app's dependency-injection registration SHALL register
`IComplianceStore` for reads, so the web app's service provider does not resolve
`IGitOpsImporter` or `IMigrationRunner`. The MySQL implementations SHALL satisfy
these abstractions. Consumers SHALL depend on the abstractions, not the concrete
implementations.

#### Scenario: Read returns persisted domain

- **WHEN** a caller invokes the `IComplianceStore` read methods after an import
- **THEN** it receives the persisted standards (with metadata), controls,
  requirements, organisations, and unified scopes with their `id`, `title`, resolved
  `subject`, one target, `disposition`, and `justification`

#### Scenario: Scope read carries the subject's narrowing metadata

- **WHEN** a caller reads the unified scopes for the web `/scopes` narrowing
- **THEN** each scope row carries, from a `LEFT JOIN` on `assets` keyed on `subject_id`, the
  subject's resolved `type` (`Company`/`Department`/`Vendor`/`Machine`, or none when the subject
  resolves to no asset), `source`, `state`, `parent`, and `owner`, so the endpoint can evaluate
  the subject-resolution predicate and the per-family readability branch (org via the accessible
  set, vendor via `owner`, machine via `parent` ancestry) server-side, and these fields are not
  surfaced in the API response

#### Scenario: Counts include requirements and one scope count

- **WHEN** a caller reads the per-kind counts after an import
- **THEN** the counts include the number of persisted requirements and one unified scope
  count alongside standards, controls, and organisations

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
requirements, whose rows reference standards; then controls; then the declared assets,
including organisations - the asset `parent` is a scalar column with no foreign key, so no
parent-before-child ordering is required among them); then replace the whole unified scope
set (delete every `scopes` row then insert the new set), which is a whole-set replacement
rather than a per-row diff because the table has a primary key plus three unique keys, so an
id-keyed pair-swap cannot be upserted safely, and the scope `subject_id` has no foreign key
while its `standard_id`/`requirement_id`/`control_id` targets are already upserted; then
replace all `maps_to` cross-ref join rows for the imported set in the `control_requirements`
join (delete the existing join rows and insert the rows derived from the new config, a
whole-set replacement rather than a per-parent diff), which is safe because controls and
requirements are both upserted by now; then delete remaining domain rows whose `id`
is absent from the config in FK-safe order (the declared assets, including organisations -
with no child-before-parent ordering required because the asset `parent` has no foreign key;
then controls; then requirements; then standards, requirements being deleted before the
standards they reference). The whole-set scope
replace precedes the absent standard, requirement, and control deletes, so a removed
standard, requirement, or control no longer has a referencing scope row when it is
deleted, and precedes the declared-asset prune, so a removed asset simply leaves a scope
with a dangling `subject_id` (no foreign key blocks the delete). After all inserts,
replacements, and deletes but BEFORE the transaction commits, the importer SHALL run the
unresolved-subject check (a `LEFT JOIN assets` applying the subject-resolution predicate) within
the same transaction so it observes the final post-write asset state, capture the unresolved
subjects into the import result, and only then commit; a failure of that check SHALL roll the
whole import back, preserving the all-or-nothing outcome. The order is foreign-key-safe not
because the config is acyclic (an asset `parent` cycle is a tolerated non-blocking warning, not a
rejection), but because the hard scope-target references (`standard`/`requirement`/`control`)
resolve and are upserted before the scope set that references them, and a tolerated asset `parent`
cycle cannot affect ordering: `parent` carries NO foreign key, so a dangling or cyclic `parent`
is a tolerated edge and never a foreign-key violation, and no parent-before-child ordering among
assets is required. A stable order therefore exists and every foreign-key constraint holds at
commit.

#### Scenario: Dropping a referenced standard in the same sync succeeds

- **WHEN** a sync removes a Standard that, in the prior persisted state, was
  referenced by a Requirement via `standard` or by a Scope targeting the standard, and the
  new config also removes those references
- **THEN** the import succeeds without a foreign-key violation, because the
  referencing rows are replaced or removed before the standard row is deleted

#### Scenario: Dropping a referenced requirement in the same sync succeeds

- **WHEN** a sync removes a Requirement that, in the prior persisted state, was
  targeted by a Scope, and the new config also removes that scope
- **THEN** the import succeeds without a foreign-key violation, because the whole scope
  set is replaced before the requirement row is deleted

#### Scenario: Removing a scope's subject asset does not block the sync

- **WHEN** a sync removes an asset that, in the prior persisted state, was the `subject` of
  a scope the new config keeps
- **THEN** the import succeeds without a foreign-key violation, because `subject_id` has no
  foreign key; the scope persists with a now-dangling subject, surfaced as a non-blocking
  warning

#### Scenario: Requirement upserted after its standard

- **WHEN** a sync imports a standard and a requirement that references it in one
  config
- **THEN** the standard row is upserted before the requirement that references it,
  and on removal the requirement is deleted before the standard

#### Scenario: Assets reconcile without a parent-before-child ordering

- **WHEN** a sync imports a company and its department in one config
- **THEN** both reconcile in the unified `assets` table; because the asset `parent` is a
  scalar column with no foreign key, no parent-before-child (or child-before-parent)
  ordering is required and neither the insert nor the delete order can violate a foreign key

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
`config` JSON value holding the type-specific settings map, a nullable `connection_id`
foreign key to `integration_connections` (set only for a `type: integration`
collector), a nullable `checks` JSON value holding an integration collector's ordered
checks list, `created_at`, and `updated_at`. The `control_id`, `vendor_id`, and
`connection_id` foreign keys SHALL be `ON DELETE RESTRICT`, matching the scope tables,
so the importer prunes referencing collectors before deleting a control, a vendor, or an
integration-connection. Identity SHALL be keyed on `id` only; the table SHALL NOT impose
a secondary uniqueness key, because a control MAY have several collectors.

The GitOps importer SHALL sync controls (with their evaluation rule) and
evidence-collectors in the same whole-set-replace transaction as the other kinds, in
a foreign-key-safe order: controls upserted by id with their `evaluation` column;
evidence-collectors upserted by id after controls, vendors, and integration-connections
are upserted (its foreign keys point at all three); absent evidence-collectors deleted
before absent integration-connections, vendors, controls, and requirements are deleted,
so no RESTRICT foreign key is violated. A blank `evaluation` SHALL be stored as NULL, a
blank `threshold` as NULL, an empty `config` map as NULL, a blank `connection` as NULL,
and an empty `checks` list as NULL; a non-empty `config` map SHALL be stored as a JSON
object and a non-empty `checks` list as a JSON array.

The read store SHALL expose the persisted evidence-collectors through the
`IComplianceStore` abstraction, SHALL include the control's `evaluation` rule (null
when unset) on the control read and the collector's `connection` (null when absent) on
the collector read, and the persisted-counts read SHALL include the evidence-collector
count.

#### Scenario: Evidence-collectors round-trip through import and read

- **WHEN** a valid config containing controls with an evaluation rule and
  evidence-collectors is imported and then read back through the store
- **THEN** every collector is persisted and returned with its `id`, `title`,
  `control`, `vendor` (null when absent), `type`, `frequency`, `threshold` (null when
  absent), `config` map, and `connection` (null when absent), and every control returns
  its `evaluation` rule (null when unset)

#### Scenario: Integration collector persists its connection and checks

- **WHEN** a `type: integration` collector naming a connection and declaring a
  non-empty checks list is imported
- **THEN** its row stores the `connection_id` foreign key and the `checks` JSON array,
  and a non-integration collector stores `connection_id` and `checks` as NULL

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

#### Scenario: Import order respects foreign keys when a referenced connection is removed

- **WHEN** an import removes an integration-connection that an evidence-collector in the
  previous persisted set names
- **THEN** the importer deletes the referencing collector before deleting the
  integration-connection, so the connection RESTRICT foreign key is not violated

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

### Requirement: Unified scope table with a subject and a polymorphic target

The store SHALL persist the unified `Scope` kind in one `scopes` table, created by merging
the previous `scopes`, `requirement_scopes`, and `vendor_scopes` tables in migration `020`.
Each scope row SHALL hold `id`, `api_version`, `title`, a `subject_id`, a nullable
`standard_id`, a nullable `requirement_id`, a nullable `control_id`, a `disposition`, a
nullable `justification`, a `created_at` set on first insert, and an `updated_at` set on
every write. The app-managed disposition routes preserve `created_at` and advance only
`updated_at`, as for every other kind. The GitOps importer does not: it replaces the whole
scope set on each sync (see below), so for a sync-authored scope `created_at` is reset to
the time of the last sync and is NOT a record of when the scope was first declared - that
authoring history lives in the config repository. No read surface exposes either column.
The `id`, `subject_id`, `standard_id`, `requirement_id`, and `control_id`
columns SHALL use binary collation (`utf8mb4_bin`) so identity is exact-byte, consistent
with `Freeboard.Core`.

The `subject_id` column SHALL have NO foreign key: it is a scalar asset reference validated
by the application and dangling-tolerated, so an asset may be retired or not-yet-discovered
without blocking a sync or the asset's deletion. The `standard_id`, `requirement_id`, and
`control_id` columns SHALL each be a foreign key `ON DELETE RESTRICT` to `standards(id)`,
`requirements(id)`, and `controls(id)` respectively, so the importer prunes referencing
scopes before deleting a targeted standard, requirement, or control. A table `CHECK`
constraint SHALL enforce that exactly one of `standard_id`, `requirement_id`, or
`control_id` is set, as a database-level backstop to the Core validator. The table SHALL
enforce at most one row per `(subject_id, standard_id)`, one per `(subject_id,
requirement_id)`, and one per `(subject_id, control_id)` with three unique keys; each key
constrains only rows whose own target column is non-null, because MySQL treats each NULL as
distinct in a unique index (the same behaviour the previous `vendor_scopes` table relied
on).

Migration `020` SHALL be forward-only and NOT atomically replay-safe (matching `015`,
`018`, and `019`): it SHALL create the unified table, copy the three source tables into it
by `INSERT ... SELECT` (mapping `scopes.organisation_id` and `requirement_scopes.organisation_id`
and `vendor_scopes.vendor_id` to `subject_id`, which already hold asset ids after the asset
unification), drop the three source tables, and leave one `scopes` table. The three source
id spaces are assumed disjoint (pre-production, no data contract); a duplicate primary key
from a colliding id SHALL fail the migration rather than silently merge two scopes.

The GitOps importer SHALL replace the whole unified scope set in one foreign-key-safe
transaction (delete-all then insert), like the previous requirement-scope and vendor-scope
replaces, ordered before the absent-standard, absent-requirement, and absent-control
deletes and before the declared-asset prune, so no target `RESTRICT` foreign key is
violated and a removed asset simply leaves a scope with a dangling subject. A blank
`justification` SHALL be stored as NULL.

Within the same import transaction, after all writes but BEFORE the commit, the importer SHALL
evaluate each persisted scope `subject` against the `assets` table with the subject-resolution
predicate (a subject is unresolved when no `assets` row has its id, or the row is a discovered
asset in the `Retired` state) - the read observes the final post-write asset state - capture the
set of unresolved subjects into the import result, and only then commit. A failure of that check
SHALL roll the whole import back, preserving the importer's all-or-nothing outcome, rather than
committing an import whose result-building then throws. The importer SHALL return the unresolved
subjects as non-blocking warnings. This DB-accurate check is the sync-path dangling-subject
signal: it covers discovered and retired `Machine` subjects that a database-less validator cannot
evaluate, and it never fails the import.

The read store SHALL expose the unified scopes through the `IComplianceStore` abstraction,
and the persisted-counts read SHALL include one scope count.

#### Scenario: Fresh database gains the unified scopes table

- **WHEN** migrations are applied to a fresh database through `020`
- **THEN** the `scopes` table exists with its primary key, a no-foreign-key `subject_id`,
  nullable `standard_id`/`requirement_id`/`control_id` foreign keys `ON DELETE RESTRICT`,
  the single-target `CHECK`, the three unique keys `(subject_id, standard_id)`,
  `(subject_id, requirement_id)`, `(subject_id, control_id)`, and binary-collation id and
  reference columns, and the `requirement_scopes` and `vendor_scopes` tables do not exist

#### Scenario: Scope persists keyed on id with a subject and one target

- **WHEN** a validated config with a `Scope` mapping a subject to a standard, a
  requirement, or a control is imported and read back
- **THEN** a `scopes` row exists keyed on the scope `id`, holding its `title`,
  `api_version`, timestamps, `subject_id`, exactly one populated target column, a
  `disposition`, and a `justification` (null when absent)

#### Scenario: Database rejects a scope with no single target

- **WHEN** a `scopes` row is written directly with zero target columns set, or with two or
  three set
- **THEN** the table `CHECK` constraint rejects the row, independently of the Core
  validator

#### Scenario: Duplicate subject-target pair violates a unique key

- **WHEN** a second scope row for an existing `(subject_id, standard_id)`, `(subject_id,
  requirement_id)`, or `(subject_id, control_id)` pair is written directly to the store
- **THEN** the database rejects it on the corresponding unique key

#### Scenario: Migration copies disjoint ids and fails on a collision

- **WHEN** migration `020` runs on a database whose `scopes`, `requirement_scopes`, and
  `vendor_scopes` ids are disjoint, and separately on one where two source rows share an id
- **THEN** the disjoint copy succeeds into the unified table, and the colliding copy fails
  `020` on the duplicate primary key rather than merging the two rows

#### Scenario: Migration marks Out rows that carry no recorded justification

- **WHEN** migration `020` copies an `Out` scope row from the legacy `scopes` or
  `requirement_scopes` table (neither of which has a `justification` column), a `vendor_scopes`
  `Out` row that recorded a real justification, and a `vendor_scopes` `Out` row whose recorded
  justification is blank
- **THEN** the two org rows land with the provenance marker "Migrated legacy Out rule;
  justification was not recorded and requires review.", the vendor row with a real justification
  keeps its own text unchanged, and the blank vendor row lands with the marker, so no copied
  `Out` row is readable without a rationale before the first sync overwrites it

#### Scenario: Counts include one scope count

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include one unified scope count alongside standards, controls,
  requirements, and organisations

#### Scenario: Importer reports a DB-unresolved scope subject as a warning

- **WHEN** an import commits a scope whose `subject` is absent from `assets`, or present only
  as a discovered asset in the `Retired` state, while another scope's subject resolves to a
  live asset
- **THEN** the import succeeds and returns the unresolved subject as a non-blocking warning,
  and does NOT report the subject that resolves to a live asset, applying the DB-accurate
  subject-resolution predicate the database-less validator cannot

