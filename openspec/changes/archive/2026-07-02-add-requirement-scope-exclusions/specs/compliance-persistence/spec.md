## ADDED Requirements

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

## MODIFIED Requirements

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
