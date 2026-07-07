## ADDED Requirements

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
