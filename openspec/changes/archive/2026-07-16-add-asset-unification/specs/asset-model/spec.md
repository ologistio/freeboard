## MODIFIED Requirements

### Requirement: Machine asset domain model in Core

The system SHALL model an integration-agnostic `Asset` in `Freeboard.Core` under
the MIT license. It SHALL define an `AssetKind` enumeration `type` whose values are
`Company`, `Department`, `Machine`, and `Vendor`, an `AssetSource` enumeration with
values `Declared` and `Discovered`, and an `AssetState` enumeration with values
`Seen` and `Retired`. An asset carries an immutable `id`, a mutable `title`, a
`type`, and a `source`. `Company`, `Department`, and `Vendor` assets are normally
`Declared`; `Machine` assets are normally `Discovered`. The discovered-only fields
`identity_kind`, `identity_value`, `state`, `first_seen`, and `last_seen` apply to
a discovered machine and are absent on a declared asset.

For a `Machine` asset the system SHALL derive an integration-agnostic identity:
given an observed hardware serial and host uuid, identity is the hardware serial
when the serial is usable, otherwise the host uuid when it is usable, otherwise no
identity. The identity is a `(kind, value)` pair where `kind` is `Serial` or
`HostUuid` and `value` is the normalized identifier. A serial SHALL be normalized
by trimming, collapsing internal whitespace, and upper-casing, and SHALL be treated
as unusable (as if absent) when the normalized value is empty or matches a fixed,
small deny-list of common blank or OEM-filler placeholder values (for example
`UNKNOWN`, `NONE`, `DEFAULT STRING`, `TO BE FILLED BY O.E.M.`). A host uuid SHALL
be normalized to its canonical form and SHALL be treated as unusable when it does
not parse as a uuid or is a firmware sentinel uuid (the all-zero uuid or the
all-ones uuid). This code SHALL NOT reference `Freeboard.Enterprise` and SHALL add
no new package dependency.

#### Scenario: Asset types and source exist

- **WHEN** the asset domain model is loaded
- **THEN** `AssetKind` has `Company`, `Department`, `Machine`, and `Vendor`
  values, `AssetSource` has `Declared` and `Discovered` values, and `AssetState`
  has `Seen` and `Retired` values

#### Scenario: Serial is the primary machine identity

- **WHEN** identity is derived from a machine observation with a non-blank
  hardware serial and any host uuid
- **THEN** the identity kind is `Serial` and the value is the normalized serial

#### Scenario: Host uuid is the fallback machine identity

- **WHEN** identity is derived from a machine observation with a blank or absent
  hardware serial and a non-blank host uuid
- **THEN** the identity kind is `HostUuid` and the value is the normalized uuid

#### Scenario: No identity when both are absent

- **WHEN** identity is derived from a machine observation with neither a usable
  hardware serial nor a usable host uuid
- **THEN** no identity is produced

#### Scenario: Placeholder serial is treated as missing

- **WHEN** identity is derived from a machine observation whose hardware serial is
  a placeholder value (blank, or a common OEM filler such as `To be filled by
  O.E.M.`) and whose host uuid is usable
- **THEN** the placeholder serial is ignored and the identity kind is `HostUuid`
  with the normalized uuid value

#### Scenario: Sentinel host uuid is treated as missing

- **WHEN** identity is derived from a machine observation with no usable serial and
  a host uuid that is a firmware sentinel (the all-zero uuid or the all-ones uuid)
- **THEN** the sentinel uuid is ignored and no identity is produced

### Requirement: Asset and asset-source schema and migration

The system SHALL persist assets in MySQL via a forward-only migration applied by
`freeboard system migrate`. The migration SHALL create one `assets` table holding
every asset - declared `Company`/`Department`/`Vendor` and discovered `Machine` -
in one id space, and SHALL merge the previous `organisations`, `vendors`, and
`asset` tables into it. A declared asset keeps its authored slug id; a discovered
asset keeps its ULID id; both live in the one `assets.id` primary key. The `assets`
table SHALL carry `type`, `source`, `title`, the nullable scalar edges `parent` and
`owner`, and the nullable discovered-only columns `identity_kind`,
`identity_value`, `state`, `hostname`, `first_seen_at`, `last_seen_at`, and
`retired_at`. Every id and reference column SHALL use `utf8mb4_bin` collation and
the columns carrying identity, enum-name, or source-key semantics (`type`,
`source`, `identity_kind`, `identity_value`, `state`) SHALL use a no-pad binary
collation (`utf8mb4_0900_bin`) so they compare by exact bytes.

The migration SHALL rewrite each discovered machine's `organisation_id` scalar to
`parent`, and SHALL re-point every enforced foreign key that named the old tables at
`assets(id)`. That complete set is `scopes.organisation_id`,
`requirement_scopes.organisation_id`,
`authz_organisation_role_assignments.organisation_id`, `vendor_scopes.vendor_id`,
`evidence_collectors.vendor_id`, and `integration_connections.vendor_id`. Missing
any one would leave a foreign key pointing at a dropped table and block the cutover,
so the migration SHALL re-point all six. Existing referencing rows keep their ids,
which resolve to the merged asset rows, so no reference is orphaned. The
`asset_source` table (one row per reporting source attachment) SHALL be retained;
its composite `(asset_id, organisation_id)` foreign key SHALL be relaxed to a simple
`asset_id -> assets(id)` foreign key, with cross-source-organisation isolation
enforced by query filtering. The tables SHALL be mutable (no append-only triggers).

The migration SHALL be forward-only and is NOT idempotent, matching the established
migration convention (the schema runner records the applied version only after the
whole file succeeds, and MySQL DDL implicit-commits per statement, so a partial
apply cannot be recovered by a naive re-run). Because this is a pre-production
cutover with no data to preserve, operational recovery from a failed run is to
restore the pre-migration database and re-run the whole migration from a clean state.

The discovered-machine identity uniqueness that the previous `asset` table enforced
per `(organisation_id, identity_kind, identity_value)` SHALL be preserved on the
merged table as a unique index on `(parent, identity_kind, identity_value)`. Because
`parent` is nullable and a unique index treats each NULL as distinct, a null-`parent`
discovered machine is not subject to this org-scoped uniqueness; ingest writes every
machine under its discovering organisation (a non-null `parent`), so the org-scoped
dedup holds on the ingest write path. Every source-filtered read of a discovered
machine SHALL keep an organisation predicate against the machine's `parent` (the
column the old `asset.organisation_id` became), so a cross-organisation or malformed
`asset_source` row cannot surface another organisation's machine now that the
composite foreign key is gone.

The migration assumes the pre-migration `organisations`, `vendors`, and `asset` id
spaces are DISJOINT. It copies rows by `INSERT ... SELECT` into the fresh `assets`
table and does not rename or reconcile a collision: a duplicate id across the three
source tables SHALL fail the migration on the duplicate primary key rather than merge
two distinct subjects into one row. This code SHALL live in the MIT
`Freeboard.Persistence` project and SHALL NOT reference `Freeboard.Enterprise` or
add any new dependency.

#### Scenario: Migration merges the three tables on a fresh database

- **WHEN** `freeboard system migrate` runs against a database with migrations
  001-018 applied
- **THEN** the merge migration applies successfully, the `assets` and
  `asset_source` tables exist, and the old `organisations`, `vendors`, and `asset`
  tables are gone

#### Scenario: Declared and discovered assets share one id space

- **WHEN** a declared Company with an authored slug id and a discovered Machine
  with a ULID id are both persisted
- **THEN** both are rows in the one `assets` table, each keyed by its own id, with
  `source` `declared` and `discovered` respectively

#### Scenario: Retargeted references resolve after the migration

- **WHEN** a `scope`, a `requirement_scope`, an `authz_organisation_role_assignment`,
  a `vendor_scope`, an `evidence_collector`, or an `integration_connection`
  referenced an organisation or vendor before the migration
- **THEN** after the migration its reference resolves to the matching asset row and
  its foreign key to `assets` is satisfied

#### Scenario: Every foreign key to a dropped table is re-pointed

- **WHEN** the migration completes
- **THEN** no foreign key remains that targets `organisations`, `vendors`, or the
  old `asset` table, so those tables can be dropped, and each of the six retargeted
  foreign keys targets `assets(id)`

#### Scenario: Colliding ids across the source tables fail the migration

- **WHEN** the merge migration runs against a database where an `organisations` id,
  a `vendors` id, and an `asset` id are not disjoint (two source tables share an id)
- **THEN** the migration fails on the duplicate primary key and does not merge the two
  distinct subjects into one `assets` row

#### Scenario: A source row cannot surface another organisation's machine

- **WHEN** a discovered machine belongs to one organisation (its `parent`) and an
  `asset_source` row names a different organisation
- **THEN** a source-filtered read for that other organisation returns no machine,
  because the read keeps an organisation predicate against the machine's `parent`

#### Scenario: Discovered dedup holds on the merged table

- **WHEN** the same machine identity (`identity_kind`, `identity_value`) is ingested
  twice under the same organisation (`parent`)
- **THEN** it resolves to one `assets` row, because the unique index on
  `(parent, identity_kind, identity_value)` carries forward the per-organisation dedup

## ADDED Requirements

### Requirement: Declared assets are synced by config; discovered assets are owned by ingest

The system SHALL reconcile declared assets from config and SHALL leave discovered
assets to ingest. A `sync` SHALL upsert every declared asset in the config and
SHALL hard-remove a declared asset whose id is absent from the config. A `sync`
SHALL NOT create, update, or delete any asset whose `source` is `discovered`, so a
config with few or zero declared assets never removes discovered inventory. Only
ingest SHALL write `source: discovered` assets; a declared config authoring
`source: discovered` SHALL be rejected as a validation error (see the
gitops-config-format capability). The declared-asset removal SHALL be
foreign-key-safe: rows referencing a removed asset (scopes, requirement-scopes,
vendor-scopes, evidence-collectors, integration-connections, org-scoped role
assignments) SHALL be pruned first.

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

### Requirement: Asset parent and owner edges with tolerated dangling references

The system SHALL model two mutually exclusive edges on an asset, at most one per
asset. `parent` is structural containment: its target MUST be a `Company` or
`Department` asset, it is carried by `Company`, `Department`, and `Machine` assets,
and it drives both scope inheritance and read-access. `owner` is accountability:
its target MUST be a `Company` or `Department` asset, it is carried by a `Vendor`
asset (a single owner; an org-wide vendor is owned at the Company root), and it
drives read-access only, not inheritance. An asset SHALL NOT carry both `parent`
and `owner`.

`parent` and `owner` SHALL be scalar references with no foreign key, validated at
write. A `parent` or `owner` that names an id absent from the resolved asset set
(a dangling reference) SHALL NOT fail a `sync` or resolution: it SHALL surface as a
NON-BLOCKING warning at sync/resolution time and in the UI. This generalizes the
prior scalar-no-foreign-key `asset.organisation_id`, so a discovered child naming a
declared parent that config later removes never lets the inventory block a config
`sync`.

A missing required edge SHALL likewise be a NON-BLOCKING warning, not an error: a
declared `Vendor` with no `owner`, and a `Machine` with no `parent`, are visible to
no caller under the fail-closed read model, so the system SHALL surface a warning
naming the asset rather than fail the sync. A `Company` or `Department` with no
`parent` is a legitimate root and SHALL NOT warn.

#### Scenario: Parent and owner are mutually exclusive

- **WHEN** an asset declares both a `parent` and an `owner`
- **THEN** validation fails, naming the asset

#### Scenario: Parent target must be Company or Department

- **WHEN** an asset's `parent` names an asset that is not a `Company` or
  `Department`
- **THEN** validation fails, naming the asset and the invalid parent target

#### Scenario: Vendor owner target must be Company or Department

- **WHEN** a `Vendor` asset's `owner` names an asset that is not a `Company` or
  `Department`
- **THEN** validation fails, naming the vendor and the invalid owner target

#### Scenario: Dangling parent is a non-blocking warning

- **WHEN** a `sync` runs where a discovered machine's `parent` names a declared
  asset that the config does not include
- **THEN** the sync succeeds, the machine is unchanged, and a non-blocking warning
  names the dangling reference

#### Scenario: Dangling owner is a non-blocking warning

- **WHEN** a `sync` runs where a vendor's `owner` names an id no asset defines
- **THEN** the sync succeeds and a non-blocking warning names the dangling owner

#### Scenario: Missing required edge is a non-blocking warning

- **WHEN** a declared `Vendor` carries no `owner`, or a `Machine` carries no
  `parent`
- **THEN** validation and `sync` succeed and a non-blocking warning names the asset
  as unreachable, while a `Company` or `Department` with no `parent` produces no
  warning

### Requirement: Asset read-access via the parent chain and the vendor owner

The system SHALL derive an asset's read-access from its edges. A rooted asset
(a `Company`/`Department`/`Machine` with a `parent` chain) SHALL be visible to a
caller who can read any organisation on its inclusive `parent` chain, using the
same subtree-union rule the authorization capability applies to the organisation
tree. A `Vendor` asset SHALL be visible to a caller who can read its `owner`
organisation. Global vendor readability - every authenticated user seeing every
vendor regardless of grants - SHALL NOT apply; a vendor with no readable `owner`
in the caller's accessible set SHALL NOT be shown to that caller.

#### Scenario: Rooted asset is visible through its parent chain

- **WHEN** a caller can read a Company and a Machine's `parent` chain reaches that
  Company
- **THEN** the caller can read that Machine

#### Scenario: Vendor is visible through its owner

- **WHEN** a caller can read a Department and a Vendor's `owner` is that Department
- **THEN** the caller can read that Vendor

#### Scenario: Vendor with an unreadable owner is hidden

- **WHEN** a caller cannot read a Vendor's `owner` organisation (or the vendor has
  no owner in the accessible set)
- **THEN** the vendor is not shown to that caller
