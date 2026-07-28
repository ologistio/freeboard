## MODIFIED Requirements

### Requirement: Declared assets are synced by config; discovered assets are owned by ingest

The system SHALL reconcile declared assets from config and SHALL leave discovered
assets to ingest. A `sync` SHALL upsert every declared asset in the config and
SHALL hard-remove a declared asset whose id is absent from the config. A `sync`
SHALL NOT create, update, or delete any asset whose `source` is `discovered`, so a
config with few or zero declared assets never removes discovered inventory. Only
ingest SHALL write `source: discovered` assets; a declared config authoring
`source: discovered` SHALL be rejected as a validation error (see the
gitops-config-format capability). The declared-asset removal SHALL be
foreign-key-safe for every FK-backed reference: the rows that reference a removed
asset through a real `ON DELETE RESTRICT` foreign key (collectors,
integration-connections, and org-scoped role assignments) SHALL be pruned first. A
`Scope`'s subject is NOT such a reference: the unified `scopes` table stores the
subject as a scalar `subject_id` column with NO foreign key, so a scope whose subject
asset is removed is LEFT DANGLING (a tolerated non-blocking warning), not pruned, and
its presence never blocks the asset delete. The unified scope table's remaining
foreign keys - its `standard_id`/`requirement_id`/`control_id` target columns - are
`ON DELETE RESTRICT` but reference `standards`/`requirements`/`controls`, not assets, so
they do not participate in declared-asset removal. The former per-target
`requirement_scopes` and `vendor_scopes` tables no longer exist (merged into `scopes` by
migration `020`), and the former `evidence_collectors` and `attestation_templates` tables
no longer exist (merged into `collectors` by the collector-merge migration), so none of
them is in the pruned set.

Because declared slug ids and discovered ULID ids share one id space, a `sync`
SHALL detect a declared asset whose id equals an existing `discovered` asset's id
and SHALL fail the sync with an error naming the id, mutating nothing, rather than
upsert over the discovered row (which would rewrite it and violate the
never-touch-discovered rule).

#### Scenario: Absent declared asset is hard-removed

- **WHEN** a `sync` runs against a config that no longer declares a
  previously-synced declared asset
- **THEN** that declared asset is deleted from the store

#### Scenario: Discovered assets survive a declared-only sync

- **WHEN** a `sync` runs against a config that declares no `Machine` assets while
  discovered machines exist in the store
- **THEN** every discovered machine remains, unchanged, after the sync

#### Scenario: Empty config does not wipe discovered inventory

- **WHEN** a `sync` runs against a config with zero assets while discovered
  machines exist
- **THEN** all declared assets are removed and all discovered machines remain

#### Scenario: Declared config cannot author a discovered asset

- **WHEN** a config document declares `kind: Asset` with `source: discovered`
- **THEN** validation fails, naming the asset, and nothing is synced

#### Scenario: Declared id colliding with a discovered id fails the sync

- **WHEN** a `sync` runs against a config that declares an asset whose id equals the
  id of an existing `discovered` asset in the store
- **THEN** the sync fails with an error naming the id and nothing in the store is
  created, updated, or deleted

#### Scenario: Removing an asset that a scope names as subject leaves the scope dangling

- **WHEN** a `sync` hard-removes a declared asset that a kept scope names as its
  `subject`
- **THEN** the asset is deleted without a foreign-key violation and the scope persists
  with a now-dangling `subject_id` (surfaced as a non-blocking warning), because the
  scope subject has no foreign key and so is not pruned before the asset delete
