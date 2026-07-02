## ADDED Requirements

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

## MODIFIED Requirements

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

### Requirement: General read store and GitOps importer abstractions

The system SHALL expose separate abstractions for reading the store and for
importing config into it. `IComplianceStore` (the general read abstraction, in the
`Freeboard.Persistence` namespace) SHALL provide read methods returning the
persisted standards (with their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata), controls (with their resolved `maps_to`
`Requirement` ids, read from the `control_requirements` join), requirements (with
their resolved owning `standard`, `theme`, `statement`, `guidance`,
`citation_label`, and `citation_url`), organisations (with resolved `parent`),
and scopes (with resolved `organisation`, `standard`, and `disposition`) and
per-kind counts that include requirements. `IGitOpsImporter` (in the
`Freeboard.Persistence.GitOps` namespace - GitOps is one writer into the general
store) SHALL provide a method that replaces the persisted set from an
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
  requirements, organisations, and scopes with their `id`, `title`, and resolved
  references

#### Scenario: Counts include requirements

- **WHEN** a caller reads the per-kind counts after an import
- **THEN** the counts include the number of persisted requirements alongside
  standards, controls, organisations, and scopes

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
not collide on the unique key); then replace all `maps_to` cross-ref join rows for
the imported set in the `control_requirements` join (delete the existing join rows
and insert the rows derived from the new config, a whole-set replacement rather
than a per-parent diff), which is safe because controls and requirements are both
upserted by now; then delete remaining domain rows whose `id` is absent from the
config in FK-safe order (requirements before standards; then organisations
child-before-parent; then controls; then standards). Because a validated config is
acyclic and has no dangling references (a `Freeboard.Core` invariant), a stable
order exists and foreign-key constraints hold at commit.

#### Scenario: Dropping a referenced standard in the same sync succeeds

- **WHEN** a sync removes a Standard that, in the prior persisted state, was
  referenced by a Requirement via `standard` or by a Scope, and the new config also
  removes those references
- **THEN** the import succeeds without a foreign-key violation, because the
  referencing rows are replaced or removed before the standard row is deleted

#### Scenario: Renaming a scope that keeps its organisation and standard pair

- **WHEN** a sync renames a Scope's `id` while keeping the same
  `(organisation, standard)` pair
- **THEN** the import succeeds and the store holds the scope under its new `id`,
  because absent scopes are pruned before the scope upsert so the unique
  `(organisation, standard)` key is free for the new row

#### Scenario: Requirement upserted after its standard

- **WHEN** a sync imports a standard and a requirement that references it in one
  config
- **THEN** the standard row is upserted before the requirement that references it,
  and on removal the requirement is deleted before the standard

#### Scenario: Parent organisation ordering holds

- **WHEN** a sync imports a company and its department in one config
- **THEN** the parent company row is upserted before the department that references
  it, and on removal the department is deleted before the parent
