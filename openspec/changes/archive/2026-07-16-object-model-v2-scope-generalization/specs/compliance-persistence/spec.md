## ADDED Requirements

### Requirement: Unified scope table with a subject and a polymorphic target

The store SHALL persist the unified `Scope` kind in one `scopes` table, created by merging
the previous `scopes`, `requirement_scopes`, and `vendor_scopes` tables in migration `020`.
Each scope row SHALL hold `id`, `api_version`, `title`, a `subject_id`, a nullable
`standard_id`, a nullable `requirement_id`, a nullable `control_id`, a `disposition`, a
nullable `justification`, a `created_at` set on first insert, and an `updated_at` set on
every write. The `id`, `subject_id`, `standard_id`, `requirement_id`, and `control_id`
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

## MODIFIED Requirements

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

## REMOVED Requirements

### Requirement: Scope disposition is unique per organisation per standard

**Reason**: Subsumed by the unified scope table, which enforces uniqueness per
`(subject_id, standard_id)` (and per requirement and control target) with three unique
keys on one merged `scopes` table.

**Migration**: The `(organisation_id, standard_id)` unique key becomes the `(subject_id,
standard_id)` unique key on the unified table; the organisation id is now the scope
`subject_id`. See the Unified scope table requirement.

### Requirement: Requirement-scope persistence

**Reason**: The dedicated `requirement_scopes` table is merged into the unified `scopes`
table by migration `020`; a requirement disposition is a scope row whose `requirement_id`
target is set.

**Migration**: `requirement_scopes` rows copy into `scopes` as `subject_id =
organisation_id`, `requirement_id = requirement_id`, with the `(subject_id, requirement_id)`
unique key preserving the old `(organisation_id, requirement_id)` uniqueness. See the
Unified scope table requirement.

### Requirement: Vendor and VendorScope persistence

**Reason**: Vendor rows are `Asset` rows of `type: Vendor` (asset unification), and the
`vendor_scopes` table is merged into the unified `scopes` table by migration `020`; a
vendor scope is a scope row whose `subject_id` is a vendor asset id and whose target is a
requirement or a control.

**Migration**: `vendor_scopes` rows copy into `scopes` as `subject_id = vendor_id`, with
`requirement_id`/`control_id`, `disposition`, and `justification` carried unchanged; the
single-target `CHECK` and the `(subject_id, requirement_id)` / `(subject_id, control_id)`
unique keys preserve the old vendor-scope invariants. See the Unified scope table
requirement.
