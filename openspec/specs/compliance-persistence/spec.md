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
`citation_label`, and `citation_url`), ONE unified ASSET set, and
scopes (each with its resolved `subject`, exactly one target of
`standard`/`requirement`/`control`, `disposition`, and `justification`) and per-kind
counts that include requirements and one unified scope count.

The unified asset read SHALL return every row of the `assets` table - `Company`,
`Department`, `Machine`, and `Vendor`, declared and discovered - each with its `id`, `title`,
`type`, `source`, `state` (null on a declared asset), and the two scalar edges `parent` and
`owner`. It SHALL REPLACE the separate organisation read and vendor read: there SHALL NOT be a
per-type read method returning organisations or vendors, because a second read model over the
same table would let the resolver and the read-access closure see different trees. Consumers
that present one type - the `/organisations` and `/vendors` endpoints, the organisation
selector, and the drill-down's vendor-title lookup - SHALL filter the one set by `type` at the
point of use.

The unified scope read SHALL NOT join the `assets` table and SHALL NOT carry any subject
metadata (`type`, `source`, `state`, `parent`, `owner`) on the scope row. Subject readability
and the live-subject predicate are both resolved by the caller against the unified asset set,
so carrying denormalized subject columns on the scope row would be a second copy of the same
facts.

`IGitOpsImporter`
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
  requirements, the one unified asset set, and unified scopes with their `id`, `title`,
  resolved `subject`, one target, `disposition`, and `justification`

#### Scenario: The asset read returns every type with both edges

- **WHEN** a caller reads the unified asset set after an import and an ingest
- **THEN** it receives one row per asset - declared `Company`, `Department`, and `Vendor`
  and discovered `Machine` alike - each carrying `id`, `title`, `type`, `source`, `state`
  (null on a declared asset), `parent`, and `owner`, ordered by `id`

#### Scenario: There is no separate organisation or vendor read

- **WHEN** the `IComplianceStore` surface is inspected
- **THEN** it exposes no method returning only organisations and no method returning only
  vendors; both are served by filtering the one unified asset read by `type`

#### Scenario: Scope read carries no subject metadata

- **WHEN** a caller reads the unified scopes
- **THEN** each row carries only its `id`, `title`, `subject`, one target, `disposition`,
  and `justification`, with no joined subject `type`, `source`, `state`, `parent`, or
  `owner`, because the caller resolves readability against the unified asset set instead

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

### Requirement: Collector persistence and Control evaluation column

The system SHALL persist the unified `Collector` kind in a single MySQL table
`collectors` and SHALL continue to persist a control's `evaluation` rule in the nullable
`evaluation` column on the `controls` table. Ids and foreign-key columns SHALL use
`utf8mb4_bin` to match Core's exact-byte id identity, consistent with the existing
compliance tables.

The `collectors` table SHALL hold `id`, `api_version`, `title`, a non-null
`control_id` foreign key to `controls`, a nullable `vendor_id` foreign key to
`assets`, a nullable `connection_id` foreign key to `integration_connections` (set only
for a `type: integration` collector), a `type`, a nullable `provider` (set only for a
`type: integration` collector), a `frequency`, a nullable `threshold` integer, a nullable
`config` JSON value holding the whole validated type-specific config map, `created_at`,
and `updated_at`. The table SHALL NOT carry a dedicated `checks` column: an integration
collector's ordered checks list is a `Checks` key inside the single `config` JSON value,
matching the authored `checks` key. The `control_id`, `vendor_id`, and `connection_id` foreign keys SHALL be
`ON DELETE RESTRICT`, matching the scope tables, so the importer prunes referencing
collectors before deleting a control, a vendor asset, or an integration-connection.
Identity SHALL be keyed on `id` only; the table SHALL NOT impose a secondary uniqueness
key, because a control MAY have several collectors.

The table SHALL carry an enforced check constraint requiring a non-null `provider` on any
row whose `type` is `integration`. The `provider` column is nullable because the other four
types require it to be absent, so nullability alone cannot express the rule; the constraint
makes a `type: integration` row with no provider impossible at rest, for the merge migration
and for every later importer write alike. The reverse half of the rule - `provider` absent
on every other type - stays a validation rule and is NOT pinned in the schema.

Every type-specific payload SHALL live inside the single `config` JSON column rather than
in dedicated columns - a `manual` or `training` collector's authored `body`, `fields`,
`pass_mark`, and `quiz` keys, and an `integration` collector's authored `checks` key - so a
config-schema change for a new type or provider is a JSON change and not a
migration. Each stored quiz item SHALL retain its answer inside the `config` JSON,
because the later grading runtime needs it; the answer lives only in storage and is never
surfaced by the read store.

The GitOps importer SHALL sync controls (with their evaluation rule) and
collectors in the same whole-set-replace transaction as the other kinds, in
a foreign-key-safe order: controls upserted by id with their `evaluation` column;
collectors upserted by id after controls, declared assets, and integration-connections
are upserted (its foreign keys point at all three); absent collectors deleted
before absent integration-connections, declared assets, controls, and requirements are
deleted, so no RESTRICT foreign key is violated. A blank `evaluation` SHALL be stored as
NULL, a blank `threshold` as NULL, a blank `provider` as NULL, and a blank `connection` as
NULL.

The stored `config` value SHALL follow one contract, stated here in full because three
things compose or read the column - the merge migration, the import plan that writes it, and
the read store's deserialization - and two more consume the typed model that read produces:

- A config with no member present SHALL be stored as SQL NULL, not as an empty JSON object.
- An otherwise non-empty config SHALL be stored as a JSON object whose keys are `Body`,
  `Fields`, `PassMark`, `Quiz`, and `Checks` - the config members' own names, which is the
  shape the pre-merge `checks`, `fields`, and `quiz` columns already persist - and each
  nested item SHALL keep the key names it already has.
- An absent member SHALL be OMITTED, never written as JSON null and never as an empty
  array; an authored empty list counts as absent, and so does a blank or whitespace-only
  `body`, which the pre-merge import already stores as NULL. So a collector with no checks
  carries no `Checks` key at all, and one whose `body` is blank carries no `Body` key.
- A `pass_mark` SHALL be stored as a JSON number, not as the raw authored text. The
  pre-merge `pass_mark` column is an integer, the import plan already parses the authored
  text to an integer before storage exactly as it does for `threshold`, and the read model
  exposes it as a nullable integer, so a number is the one value shape all three already
  agree on.
- Each stored quiz item SHALL retain its answer.

This storage shape is private to the store: the read API's snake_case keys are produced by
the endpoint projection, and the two shapes are deliberately allowed to differ. Three
parties compose or read the column itself and SHALL agree on its key literals - the merge
migration, the import plan, and the read store's deserialization. Two further parties
consume the typed read model derived from it and agree only on the member SET, not on the
stored spelling: the endpoint projection maps each member to a snake_case wire key, and the
scheduler's config fingerprint serializes the whole read model under its own emission rules
(see the collector-scheduler capability).

The read store SHALL expose the persisted collectors through the `IComplianceStore`
abstraction as one collector read, SHALL include the control's `evaluation` rule (null
when unset) on the control read and the collector's `provider` and `connection` (each null
when absent) on the collector read, and the persisted-counts read SHALL include one
collector count in place of the removed separate evidence-collector and
attestation-template counts. The read store SHALL deserialize the `config` JSON back into
its typed shape and SHALL project each quiz item to an answer-free shape at the store
boundary, so no read surface (API, CLI, or web register) can expose a training quiz's
correct answer.

#### Scenario: Collectors round-trip through import and read

- **WHEN** a valid config containing controls with an evaluation rule and collectors of
  several types is imported and then read back through the store
- **THEN** every collector is persisted and returned with its `id`, `title`,
  `control`, `vendor` (null when absent), `type`, `provider` (null when absent),
  `frequency`, `threshold` (null when absent), typed `config`, and `connection` (null when
  absent), and every control returns its `evaluation` rule (null when unset)

#### Scenario: Integration collector persists its provider, connection, and checks

- **WHEN** a `type: integration` collector naming a provider and a connection and
  declaring a non-empty `config` checks list is imported
- **THEN** its row stores the `provider`, the `connection_id` foreign key, and the checks
  as a `Checks` array inside its `config` JSON, and a non-integration collector stores
  `provider` and `connection_id` as NULL and carries no `Checks` key in its `config`

#### Scenario: Every type-specific payload persists in the config column

- **WHEN** a `manual` collector with `fields`, a `training` collector with a
  `pass_mark` and `quiz`, and an `integration` collector with `checks` are imported
- **THEN** each row stores its whole payload inside the single `config` JSON column and no
  dedicated `body`, `fields`, `pass_mark`, `quiz`, or `checks` column exists on the table

#### Scenario: The stored config follows the storage contract

- **WHEN** a `training` collector authoring a `pass_mark` of 90 and a `quiz` but no `body`
  and no `fields`, and a `script` collector authoring no `config` at all, are imported and
  their rows inspected
- **THEN** the training collector's `config` is a JSON object carrying `PassMark` as the
  number 90 and a `Quiz` array, with no `Body`, `Fields`, or `Checks` key present in any
  form, and the script collector's `config` column is SQL NULL rather than an empty JSON
  object

#### Scenario: Read store redacts the quiz answer

- **WHEN** a training collector with a quiz `answer` is imported and then read back
  through the store
- **THEN** the returned quiz items expose their `prompt` and `options` but carry no
  `answer`, while the stored `config` JSON still contains the answer for later grading

#### Scenario: Import order respects foreign keys when a targeted control is removed

- **WHEN** an import removes a control that a collector in the previous persisted set
  attaches to
- **THEN** the importer deletes the referencing collector before deleting the
  control, so the control RESTRICT foreign key is not violated

#### Scenario: Import order respects foreign keys when a named vendor asset is removed

- **WHEN** an import removes a vendor asset that a collector in the previous persisted set
  names
- **THEN** the importer deletes the referencing collector before the declared-asset prune,
  so the vendor RESTRICT foreign key is not violated

#### Scenario: Import order respects foreign keys when a referenced connection is removed

- **WHEN** an import removes an integration-connection that a collector in the previous
  persisted set names
- **THEN** the importer deletes the referencing collector before deleting the
  integration-connection, so the connection RESTRICT foreign key is not violated

#### Scenario: Evaluation rule is added to an existing control without data loss

- **WHEN** a control that previously had no `evaluation` is re-synced with an
  `evaluation` rule
- **THEN** the stored control row returns the new rule and its other columns and
  cross-references are unchanged

#### Scenario: Counts include one collector count

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include one number for persisted collectors and no separate
  evidence-collector or attestation-template count

### Requirement: Collector merge migration

The system SHALL merge `evidence_collectors` and `attestation_templates` into the single
`collectors` table with one forward-only migration applied by `freeboard system migrate`,
following the established forward-only, not-atomically-replay-safe convention of the
preceding merge migrations (MySQL DDL implicit-commits per statement and the runner
records `schema_migrations` only after the whole file succeeds, so operational recovery is
restore-and-rerun; this is a pre-production hard cutover, so no data contract is at risk).
The migration SHALL assume the two source id spaces are DISJOINT: a colliding id SHALL
fail on the duplicate primary key rather than silently merging two collectors.

The migration SHALL:

- before creating anything, add a named, enforced check constraint to `evidence_collectors`
  that expresses the pre-merge type-conditional `checks`/`connection_id` rule IN FULL, and
  immediately drop it again. Because the rule is expressed in full, the guard refuses two
  classes of source row, not one: a row whose `type` is not `integration` and which carries a
  non-null `checks` value or a non-null `connection_id`, AND a `type: integration` row whose
  `checks` is not a non-empty JSON array OF OBJECTS WITH WELL-TYPED KNOWN MEMBERS - which
  covers a null `checks` (the shape
  of every
  integration row written before the migration that added the column, whose `connection_id`
  is null too), an empty array, a JSON null, a value that is not an array, an array
  carrying an item that is not a JSON object, and an array carrying an object item whose
  `SourceKey`, `Name`, or `Severity` is present with a JSON type other than string. The guard
  SHALL express requiredness at the LIST level, SHALL constrain each item to a JSON
  OBJECT, and SHALL constrain those three known members to the JSON string type and nothing
  else - no requiredness, no token set, and no uniqueness - so it SHALL NOT enforce a check
  item's FIELD RULES: an item whose known members are absent, or present and well-typed but
  failing a field rule, travels
  inside a value the migration carries verbatim and still binds on read, so it falls
  under the scope limit stated below, whereas a non-object item and a wrong-typed or null
  known member do not bind at all. Adding
  the constraint
  validates the existing rows and fails the migration by name when one breaks it; dropping
  it again keeps the file replayable after the operator's repair, because neither a failed
  nor a successful add leaves a constraint behind. This guard runs before the `collectors`
  table exists, so a refused migration leaves the schema untouched;
- add, and immediately drop, a second named, enforced check constraint on
  `attestation_templates` applying the SAME read-boundary rule to `fields` and `quiz`: each
  SHALL be SQL NULL or a JSON array whose every item is a JSON OBJECT, and each known member
  of an item - `Id`, `Label`, `Type`, `Options` on a field; `Id`, `Prompt`, `Answer`,
  `Options` on a quiz item - SHALL, when present, carry its declared JSON type, with
  `Options` an array of strings. The scope is the same as the `evidence_collectors` guard's
  and stops in the same place: no requiredness, no token set, no uniqueness, and - unlike the
  integration `checks` list, which the merged rules require to be non-empty - no minimum
  length, because an empty array reads back cleanly and only carries the cosmetic defect of a
  member the storage contract would have omitted. This guard is required for the same reason
  the first one is: the migration re-shapes both columns into `config` by exactly the
  mechanism it re-shapes `checks`, carrying the column value across as an opaque JSON value,
  so the same shapes reach the same typed read and fail it the same way. Guarding one
  re-shaped column and not the other two would leave the read guarantee stated below true for
  integration collectors alone. It also makes the empty-config test in the template copy step
  EXACT: a JSON null literal is not SQL NULL, so without this guard such a value would pass
  the all-columns-null test and then be deleted as a null-valued member, landing an empty JSON
  object where the storage contract requires SQL NULL. Only `fields` and `quiz` can hold a
  JSON null (the other two source columns are not JSON-typed), and an array schema rejects
  one;
- create `collectors` with its columns, indexes, the integration-provider check constraint,
  and the three `ON DELETE RESTRICT`
  foreign keys, using fresh constraint names that cannot collide with the still-present
  source tables' schema-wide constraint names;
- copy every `evidence_collectors` row, carrying its `id`, `api_version`, and `title`
  (all three NOT NULL on `collectors`), re-tokenizing `type` so `manual-attestation`
  becomes `manual` and `training-attestation` becomes `training` (every other token
  unchanged), and deriving `provider` for a `type: integration` row from the `provider` of
  the `integration_connections` row its `connection_id` names (NULL for any other type),
  composing the new `config` JSON object from the source row's old `checks` column alone
  under the stored `Checks` key, and carrying `frequency`, `threshold`, both timestamps, and
  ALL THREE foreign-key columns - `control_id`, `vendor_id`, and `connection_id` - unchanged;
- copy every `attestation_templates` row, carrying its `id`, `api_version`, `title`,
  `control_id`, `type`, `created_at`, and `updated_at` (all NOT NULL on both tables, and the
  source `type` is already `manual` or `training`, so carrying it verbatim is what keeps the
  integration-provider check constraint from firing on this path) and composing
  its `body`, `fields`, `pass_mark`, and
  `quiz` columns into the single `config` JSON object under the stored `Body`, `Fields`,
  `PassMark`, and `Quiz` keys, omitting an absent member rather
  than writing it as a JSON null, leaving `vendor_id`, `connection_id`, `provider`, and
  `threshold` NULL, and writing a placeholder `frequency` because the source
  table has no cadence column;
- re-point the `collector_credentials` collector foreign key at `collectors (id)`,
  preserving its `ON DELETE CASCADE`, BEFORE dropping the legacy tables - dropping
  `evidence_collectors` while the credential constraint still references it fails;
- drop `evidence_collectors` and `attestation_templates`.

The migration SHALL compose the new `config` from schema-owned source data ONLY - the old
`checks` column and the four template form columns - and SHALL NOT carry the pre-merge
free-form `config` map into it. That map is a string-to-string dictionary that accepts any
ad-hoc key: persisting it would put keys the new schema does not register, potentially
including credential-shaped ones, into a column this capability declares closed, and a
carried value that collides with a typed config member (a `pass_mark` of `"90"`, or a key
named for the checks, fields, or quiz list) would fail to bind when the read store
deserializes the column, taking every collector read to an unreachable-store response until
a sync rewrote the row. An operator who authored free-form `config` keys SHALL hand-migrate
them into the authored documents with the rest of the hand-migration; the authored YAML is
untouched by the migration and `gitops sync` writes whatever the new schema registers.

BOTH `config` compositions SHALL be NULL-safe at the source, because the JSON object
constructor writes a JSON null for a NULL column rather than returning NULL. A copied
`evidence_collectors` row whose `checks` is NULL SHALL land with `config` NULL, not with a
`Checks` key whose value is JSON null; a source row whose `checks` is present SHALL land
with those checks intact regardless of what the old free-form `config` column held,
including when that column is NULL - the common integration-collector shape. A copied
`attestation_templates` row whose `body`, `fields`, `pass_mark`, and `quiz` are ALL NULL
SHALL likewise land with `config` NULL, not with an empty JSON object, matching the storage
contract's rule that an empty config is stored as SQL NULL.

The composed `config` SHALL satisfy the storage contract in full, not only in its key
names: the stored keys are `Body`, `Fields`, `PassMark`, `Quiz`, and `Checks` - the config
members' own names, the same phrasing the storage contract above uses - matching what the
pre-merge
`checks`, `fields`, and `quiz` columns already store, so the migration carries each of those
column values across as an opaque JSON value rather than rewriting the keys of every nested
item; and the copied `pass_mark` stays the integer the source column holds, which is the
same value shape the import plan writes. The API's snake_case wire keys are produced by the
endpoint projection, not by the storage shape.

The migration SHALL NOT add a foreign key from `collector_scheduler_state.collector_id` or
from `evidence_runs.collector_id` to `collectors`: both are deliberately scalar with no
foreign key so scheduler state and appended evidence survive collector churn.

The migration SHALL FAIL rather than copy an `evidence_collectors` row whose `type` is not
`integration` and which carries a non-null `checks` value or a non-null
`connection_id`. The copy step composes the merged `config` from the source `checks` column
and carries `connection_id` verbatim, without a type test of its own, so copying such a row
would land a `Checks` config key on a pair whose registered schema does not name it, or a
`connection` on a collector whose type may not carry one - both states the merged rules
reject. The pre-copy source guard is what fails it, by name, before the `collectors` table
is created at all. The copy step SHALL NOT instead drop the offending value: silently
stripping it is the same disposition this capability already refuses for the pre-merge
free-form `config` map, and it would leave a hard-cutover database holding a row the merged
contract rejects with no signal that it did.

The migration SHALL likewise FAIL rather than copy a `type: integration`
`evidence_collectors` row whose `checks` is not a non-empty JSON array of objects with
well-typed known members. The same
pre-copy
source guard is what fails it, by name and before anything is created, because the guard
expresses the pre-merge rule in full and that rule requires a non-empty checks list on an
integration collector, every item of which the config loader normalizes to a non-null check
before validation runs. Each refused shape would otherwise land a merged row the target
contract cannot hold: a null `checks` copies as a null `config` on a collector whose
registered schema makes `checks` required; an empty array or a JSON null copies as a member
the storage contract requires to be omitted; a value that is not an array copies as a
`Checks` member the typed read cannot bind, taking every collector read to an
unreachable-store response until a sync rewrites the row; an array carrying a non-object
item lands the same outcome one level in - a scalar item does not bind to a check, and a JSON
null item binds to a list with a null element that the read surfaces then dereference; and an
object item carrying a wrong-typed known member lands it one level further in still, because
the typed read IGNORES a property it does not match but FAILS on one it cannot convert, so a
numeric `SourceKey` is the same read failure as a scalar item, while a JSON-null known member
binds a null into a member the read model treats as non-null. A null
`checks` is also the shape
of every `type: integration` row written before the migration that added the column, in which
`connection_id` is null as well, so such a row is refused HERE rather than later by the
integration-provider constraint.

The migration SHALL likewise FAIL rather than copy an `attestation_templates` row whose
`fields` or `quiz` is not SQL NULL and not a JSON array of objects whose known members are
well-typed. The template source guard is what fails it, by name and before anything is
created. The rule is the same one, applied to the other two columns the migration re-shapes:
a non-array value, a non-object item, and an object item whose known member carries the wrong
JSON type or a JSON null each copy into `config` as a member the typed read cannot bind or
binds as a null the read model treats as non-null, taking every collector read to an
unreachable-store response until a sync rewrites the row. A JSON null in either column is
refused for a second reason as well: it is not SQL NULL, so it would pass the template copy
step's all-columns-null test and then be dropped as a null-valued member, landing an empty
JSON object where the storage contract requires SQL NULL.

The token-type rules across both guards - an item must be a JSON object, and a present known
member must carry its declared JSON type - are what make the read guarantee stated below TRUE
rather than assumed. An item whose known members are ABSENT still binds, because a missing
JSON member leaves the record's own empty default in place; that holds for an OBJECT item
whose present known members carry the right JSON type, and for no other shape. The config
loader normalizes a null check item on the AUTHORING
path, but the persistence read path is a plain typed deserialize with no equivalent
normalization and no converter, so a stored column value that never came through the loader
carries no such guarantee. The guards are what confine every re-shaped column to the shapes
that bind.

The migration SHALL FAIL rather than copy an `evidence_collectors` row whose `type` is
`integration`, whose `checks` satisfies the guard, and whose `connection_id` is NULL, because
such a row cannot derive a
`provider` and the merged rules forbid an integration collector without one. The
integration-provider check constraint on `collectors` is what fails it, so the error names
the violated rule. This failure SHALL occur on the first copy step, before the credential
foreign key is re-pointed and before either legacy table is dropped, so a refused migration
leaves both source tables and their data intact. Of the four REFUSALS this is the only one
on which a
partially created `collectors` table exists to be removed before the re-run; a colliding id
is not a refusal but it likewise aborts on a copy step and likewise leaves one, and in both
cases removing that table is valid only BEFORE the credential foreign key is re-pointed,
since afterwards the live constraint blocks the drop.

Refusing is deliberate and is the same standard the migration already applies to a colliding
id. Every refused row is already invalid under the PRE-merge rules - which require a
`connection` and a non-empty `checks` list on an integration collector, forbid a
`connection` or a `checks` list on every other
type, and require every form field and quiz item to be an object with typed members - so each
can only be a stale row that predates the columns it uses or was written
outside the importer; all are repairable before migrating, by a `gitops sync` against the
pre-merge schema or by deleting the row.

Every refusal SHALL be expressed as a named check constraint, so the failure
identifies the violated rule rather than surfacing as a generic copy error. The three
constraints SHALL differ in lifetime, deliberately: the integration-provider constraint is a
permanent constraint on `collectors`, because it also holds for every later importer write,
while both pre-copy source guards are transitional and SHALL NOT be pinned on `collectors`,
because which `(type, provider)` pair may carry a given `config` key, and whether that key is
required, are owned by the config-schema
registry and pinning either in the schema would make a future registry change a migration.

The refusal SHALL be bounded by that reasoning and SHALL NOT be extended in any of three
directions. It SHALL NOT extend to a pre-merge-invalid value in a column the migration
carries VERBATIM - a `type` token outside the set, a blank `frequency`, an out-of-range
`threshold` - because the migration neither re-shapes nor derives from those columns, so
such a value is equally invalid before and after and `gitops validate` reports it in the
same terms either way. A check item that IS a JSON object and whose known members are ABSENT,
or present with their declared JSON type but failing a FIELD rule (a severity outside the
token set, a duplicate name or source key), inside an otherwise well-formed `checks`
array, falls under that same limit and SHALL be copied, not refused: the array travels into
`config` as an opaque value, so the item's fields are carried verbatim, they still read back
without error, and `gitops validate` names them before and after in the same terms. An item
that is not a JSON object, and an object item whose known member is present with the wrong
JSON type or as a JSON null, are NOT within that limit and are refused above, because neither
reads back at all. Both limits apply identically to a `fields` or `quiz` item. The
refusals above are the cases where the migration's OWN
re-shaping is what would turn an already-invalid source row into a merged row: the
derivation of `provider` from `connection_id`, and the move of `checks`, `fields`, and `quiz`
into `config`. It
SHALL NOT extend to a state the source schema cannot express:
`attestation_templates` has no cadence column at all, so a migrated attestation's
`frequency` is a documented placeholder rather than a refusal, and free-form `config` keys
an operator has not yet hand-migrated live in the authored documents rather than in any
source column, so the migration cannot see them and `gitops validate` is what reports them.
And it SHALL NOT extend to a row that was VALID under the pre-merge rules and that only the
MERGED rules reject, where the missing value is structurally absent from that source row and
the operator has no pre-merge authoring shape in which to supply it. Refusing there would
refuse a healthy database, which is the opposite of what the refusal exists for. Such a row
SHALL be copied as it stands, on three conditions that this increment meets:

- it SHALL still read back through the store without error - a NULL `config` deserializes
  to an empty typed config through the same path a collector whose schema registers no key
  takes, so no read surface fails and no redaction is bypassed;
- nothing SHALL consume the value the row is missing. It SHALL NOT be scheduled, which
  holds because only a `type: integration` collector is claimed, and no runtime SHALL read
  the absent value, which holds because no attestation grading runtime exists in this
  increment. This condition is deliberately narrower than "is not acted on at all", which
  would be false: a copied `manual` or `training` row keeps its `vendor_id` and its
  credentials are re-pointed at `collectors`, so it MAY still ingest evidence. That is
  preserved behaviour, not an exception - evidence ingest does not gate on a collector's
  `type` (see the evidence-ingest capability) - and it does not depend on the missing
  value: ingest reads the collector's vendor, its control's mapped requirements, and its
  own authored cadence, all of which the copied row carries, and never its `config`. An
  operator ingesting in the window before the sync therefore observes exactly the pre-merge
  behaviour: the run is appended and stamped with the collector's own authored cadence;
- it SHALL be resolved by the required `gitops sync`. Resolution is by upsert-and-prune,
  not repair in place: the sync writes the one hand-migrated document and prunes the
  surplus row of the pair as absent. Because `collector_credentials` cascades on delete,
  the operator SHALL keep the id of the row that a credential hangs off, or that
  credential is revoked by the prune and the ingesting collector's next POST is rejected as
  unauthenticated. Appended runs are unaffected either way, because
  `evidence_runs.collector_id` carries no foreign key.

The migration MAY still produce a row that the collector validation rules would reject, and
the required `gitops sync` is what resolves it:

- The placeholder `frequency` written for a migrated attestation SHALL be a valid cadence
  token, SHALL be the longest cadence in the vocabulary, and SHALL be overwritten by the
  next `gitops sync` from the authored config. It is NOT inert before the sync: the read
  API, the register page, and the CLI collector listing all display a migrated
  attestation's cadence, so until the sync runs they report a cadence that no authored
  document backs. The drill-down projection is not among them, because an attestation-tagged
  check is projected with no cadence at all. Choosing the longest cadence keeps that
  pre-sync display permissive
  rather than alarm-generating. The placeholder is NOT reachable through evidence staleness
  at migration time: only rows copied from `attestation_templates` receive it, that table
  has no vendor column and its rows can hold no collector credential, and evidence ingest
  requires a collector with a vendor - the rows that CAN ingest are copied from
  `evidence_collectors` and carry their own authored cadence.
- An attestation authored as a PAIR - an `evidence_collectors` row of type
  `manual-attestation` or `training-attestation` carrying the cadence and the optional
  vendor, and an `attestation_templates` row carrying the form, on the same control - SHALL
  land as TWO collector rows on that control, because nothing in the source schema links the
  two: neither table carries a reference to the other, the pairing is the shared
  `control_id` alone, and neither side is required, so a control may carry either without
  the other or several of both. The migration SHALL NOT attempt to merge the pair by shared
  `control_id`, because the pairing is not a key: a control carrying two templates and one
  collector, or two collectors and one template, has no pairing the source data expressed,
  and the surviving `id` - the identity that credentials, scheduler state, and appended
  evidence are all keyed on - would be an arbitrary choice. One consequence is that a
  migrated `training` collector holds neither `pass_mark` nor `quiz` until the sync, a state
  the collector validation rules reject; it is copied rather than refused because its source
  row was valid and `evidence_collectors` has no column that could have carried the form.
  Until the sync runs, the read API, the register page, and the drill-down show two entries
  where the authored config declares one, and the training entry shows no quiz. The row
  SHALL still read back through the store without error - an absent `config` deserializes to
  an empty typed config, the same path a `script` collector takes - and SHALL NOT be
  scheduled, because only a `type: integration` collector is. It MAY still ingest evidence
  if it carries a vendor and holds a credential, which is unchanged behaviour that does not
  read the missing form. The `gitops sync` upserts the
  single hand-migrated document and prunes the surplus row as absent.

That sync is therefore a REQUIRED step of the migration, not an optional follow-up, and the
migration header SHALL say so. The header SHALL also advise dumping both source tables
before migrating: the authored config remains the source of truth, but the dropped
`evidence_collectors` table is the only remaining copy of the pre-merge free-form `config`
map, which an operator part-way through the hand-migration may still need to read.

#### Scenario: Migration applies cleanly on a fresh database

- **WHEN** `freeboard system migrate` runs against a database migrated to the prior
  ordinal
- **THEN** the migration applies successfully, the `collectors` table exists with its
  `provider` and `config` columns and its three foreign keys, and neither
  `evidence_collectors` nor `attestation_templates` exists

#### Scenario: Legacy collector rows are copied with re-tokenized types

- **WHEN** the migration runs against a database holding `evidence_collectors` rows of
  types `integration`, `script`, `agent`, `manual-attestation`, and `training-attestation`
- **THEN** each row appears in `collectors` with its id, api version, title, references,
  threshold, and both timestamps intact, its old `checks` column composed into a `config`
  JSON object carrying the checks under the stored `Checks` key, and with `type`
  re-tokenized to `integration`, `script`, `agent`, `manual`, and `training` respectively

#### Scenario: Checks survive a NULL free-form config and a NULL checks column yields no config

- **WHEN** the migration copies a `type: integration` `evidence_collectors` row whose old
  free-form `config` column is NULL and whose `checks` column holds a non-empty array, and a
  `type: script` row where both columns are NULL
- **THEN** the first row lands with its checks intact under the `config` `Checks` key, and
  the second lands with `config` NULL rather than with a `Checks` key set to JSON null

#### Scenario: A pre-merge free-form config key is not carried into the merged column

- **WHEN** the migration copies an `evidence_collectors` row whose old free-form `config`
  column holds keys the new `(type, provider)` schema does not register
- **THEN** the copied row's `config` carries none of those keys, so the merged column holds
  only schema-owned data and the typed read of the column cannot fail on a carried value

#### Scenario: Integration rows derive their provider from the referenced connection

- **WHEN** the migration copies a `type: integration` row whose `connection_id` names an
  integration-connection with provider `fleet`
- **THEN** the copied row's `provider` is `fleet`, and a copied row of any other type has a
  NULL `provider`

#### Scenario: An integration row with checks but no connection fails the migration

- **WHEN** the migration copies a `type: integration` row whose `checks` is a non-empty JSON
  array, so it passes the pre-copy guard, but whose `connection_id` is NULL, so no
  `provider` can be derived for it
- **THEN** the migration fails on the integration-provider check constraint, the migration
  version is not recorded, and both legacy tables still exist with their rows intact,
  because the copy step that fails runs before the credential re-point and before either
  drop

#### Scenario: A non-integration row carrying checks or a connection fails the migration

- **WHEN** the migration runs against a database holding an `evidence_collectors` row whose
  `type` is `manual-attestation` and whose `checks` column is non-null, or one whose `type`
  is `script` and whose `connection_id` is non-null
- **THEN** the migration fails on the named pre-copy source guard, the migration version is
  not recorded, the `collectors` table was never created, and both legacy tables still exist
  with their rows intact - rather than copying the row with a `Checks` config key or a
  `connection_id` the merged rules reject, and rather than silently dropping the offending
  value

#### Scenario: An integration row whose checks is absent or malformed fails the migration

- **WHEN** the migration runs against a database holding a `type: integration`
  `evidence_collectors` row whose `checks` column is SQL NULL - the shape of every
  integration row written before the migration that added the column, whose `connection_id`
  is NULL as well - or is an empty JSON array, or is a JSON null, or holds a JSON value that
  is not an array
- **THEN** each of those rows fails the migration on the same named pre-copy source guard,
  the migration version is not recorded, and the `collectors` table was never created - so
  the null-`checks` pre-existing row is refused by the source guard rather than by the
  integration-provider constraint

#### Scenario: An integration row whose checks array holds a non-object item fails the migration

- **WHEN** the migration runs against a database holding a `type: integration`
  `evidence_collectors` row whose `checks` column is a non-empty JSON array one of whose
  items is a scalar, or a JSON null, or a nested array rather than a JSON object
- **THEN** each of those rows fails the migration on the same named pre-copy source guard,
  the migration version is not recorded, and the `collectors` table was never created -
  because such an item does not bind at the read boundary at all, unlike an object item whose
  fields are mis-shaped

#### Scenario: An integration row whose check item holds a wrong-typed known member fails the migration

- **WHEN** the migration runs against a database holding a `type: integration`
  `evidence_collectors` row whose `checks` column is a non-empty JSON array of JSON objects,
  one of which carries a `SourceKey`, `Name`, or `Severity` whose JSON value is a number, a
  boolean, an array, an object, or a JSON null rather than a string
- **THEN** each of those rows fails the migration on the same named pre-copy source guard,
  the migration version is not recorded, and the `collectors` table was never created -
  because the typed read fails on a present member it cannot convert, unlike an unmatched
  member, which it ignores

#### Scenario: A check item whose fields are mis-shaped is copied rather than refused

- **WHEN** the migration copies a `type: integration` row whose `checks` is a non-empty JSON
  array of JSON OBJECTS whose known members are absent, or are present as strings but do not
  satisfy the severity token set or the name and source-key uniqueness rules
- **THEN** the pre-copy guard passes, the row is copied with its checks array carried
  verbatim under the `config` `Checks` key, and the row reads back through the store without
  error as a check with empty or unvalidated members - the guard constrains the item's type
  and its known members' JSON types, never their presence, their token set, or their
  uniqueness, and `gitops validate` is what names the item

#### Scenario: A template row whose fields or quiz is malformed fails the migration

- **WHEN** the migration runs against a database holding an `attestation_templates` row whose
  `fields` or `quiz` column holds a JSON value that is not an array, or a JSON null, or an
  array one of whose items is a scalar, a JSON null, or a nested array, or an array of objects
  one of which carries a known member - `Id`, `Label`, `Type`, `Options`, `Prompt`, or
  `Answer` - whose JSON type is not the declared one
- **THEN** each of those rows fails the migration on the named template source guard, the
  migration version is not recorded, and the `collectors` table was never created - because
  the migration re-shapes both columns into `config` by the same mechanism it re-shapes
  `checks`, so the same shapes reach the same typed read and fail it the same way

#### Scenario: A template row whose form item is merely incomplete is copied rather than refused

- **WHEN** the migration copies an `attestation_templates` row whose `fields` or `quiz` is an
  empty array, or an array of JSON OBJECTS whose known members are absent or are present with
  their declared JSON type but fail a field rule
- **THEN** the template source guard passes and the row is copied with the column value
  carried verbatim under its `config` key, because the guard constrains an item's type and its
  known members' JSON types and nothing else - neither list is required by the merged rules,
  so an empty one is not refused either, and `gitops validate` is what names the item

#### Scenario: A valid row passes the pre-copy guards

- **WHEN** the migration runs against a database whose non-integration `evidence_collectors`
  rows all carry a NULL `checks` and a NULL `connection_id`, alongside integration rows that
  carry a non-empty `checks` array of JSON objects and a `connection_id`, and whose
  `attestation_templates` rows carry a NULL or well-typed `fields` and `quiz`
- **THEN** both pre-copy guards pass, neither leaves a constraint behind on the table it
  guarded, and the migration proceeds to the copy steps

#### Scenario: An id shared by the two source tables aborts the migration

- **WHEN** the migration runs against a database in which an `evidence_collectors` row and an
  `attestation_templates` row share an id - a state a valid pre-merge config can produce,
  because duplicate-id detection runs per kind and each source table has its own primary key
- **THEN** the migration aborts on the merged table's primary key, the migration version is
  not recorded, and both legacy tables and their rows are intact. The failure names a key
  rather than a rule, and it leaves a partially created `collectors` table holding the first
  copy step's rows; the repair is to rename one id in the authored config, sync against the
  pre-merge schema, remove that table, and re-run

#### Scenario: Legacy templates become manual and training collectors

- **WHEN** the migration runs against a database holding `attestation_templates` rows,
  including one carrying a `pass_mark` and one whose `body`, `fields`, `pass_mark`, and
  `quiz` columns are all NULL
- **THEN** each row appears in `collectors` with its `control_id`, `api_version`, `title`,
  `type`, and both timestamps intact, its
  `body`, `fields`, `pass_mark`, and `quiz` composed into the single `config` JSON object
  with absent members omitted rather than written as JSON null and the pass mark carried as
  a JSON number, the all-NULL row landing with `config` SQL NULL rather than an empty JSON
  object, and every row carrying a valid placeholder `frequency`

#### Scenario: A paired attestation collector and template land as two collectors

- **WHEN** the migration runs against a database holding an `evidence_collectors` row of
  type `training-attestation` and an `attestation_templates` row of type `training` attached
  to the SAME control
- **THEN** the migration succeeds and both rows appear in `collectors` as `type: training`
  on that control - the first with its authored `frequency`, its vendor, and a SQL NULL
  `config`, the second with the form in its `config` and the placeholder `frequency` - and
  the form-less row reads back through the store without error even though the collector
  validation rules would reject it

#### Scenario: Collector credentials survive the merge and still cascade

- **WHEN** the migration runs against a database holding `collector_credentials` rows and
  the merged collector is later deleted
- **THEN** the migration re-points the credential foreign key before dropping
  `evidence_collectors`, so the drop succeeds; the credential rows still resolve to their
  collector after the merge and are removed by the re-pointed `ON DELETE CASCADE`

#### Scenario: Scheduler state and evidence runs gain no foreign key

- **WHEN** the migration completes
- **THEN** `collector_scheduler_state.collector_id` and `evidence_runs.collector_id` still
  carry no foreign key, so a row for a since-deleted collector neither blocks a delete nor
  is cascaded away

### Requirement: Statement of Applicability input snapshots read one asset set

The store SHALL expose the Statement of Applicability inputs as two repeatable-read
snapshots - a flat snapshot for the JSON endpoint and a drill-down snapshot for the view page -
each of which SHALL read the ONE unified asset set exactly once and SHALL NOT read a separate
organisation list, a separate vendor list, or a separate resolvable-asset-id list.

The flat snapshot SHALL carry the assets, the unified scopes, and the requirements. The
drill-down snapshot SHALL carry those three plus the controls (with resolved `maps_to`) and the
unified collectors. In both snapshots the resolution tree, the live-subject predicate for the
dangling-subject warning (an asset row exists and is not a discovered asset in the `Retired`
state), and - in the drill-down - the vendor titles SHALL all be derived from that one asset
list, so the projection cannot resolve over one view of the assets while warning against
another. Every list in a snapshot SHALL be read inside one transaction at repeatable-read
isolation, so the snapshot cannot straddle a concurrent importer commit.

#### Scenario: Both snapshots read the assets once

- **WHEN** a caller reads either Statement of Applicability input snapshot
- **THEN** the result carries one unified asset list and no separate organisation list,
  vendor list, or resolvable-asset-id set, and every list in the snapshot was read in one
  repeatable-read transaction

#### Scenario: The warning predicate and the tree come from one read

- **WHEN** a snapshot is read while an importer concurrently removes an asset that a scope
  names as its subject
- **THEN** the resolution and the dangling-subject warning agree, because both are computed
  from the same asset list in the same snapshot

