# asset-model Specification

## Purpose

The asset-model capability defines the integration-agnostic asset that evidence
collectors and discovery sources resolve to, so per-machine evidence has a stable
target. It governs the `Machine` asset kind, org-scoped serial/host-uuid identity
derivation and dedup (one machine per organisation regardless of how many sources
report it), per-source attachment by `(source, external_id)`, the seen/retired
lifecycle, and the read/write persistence stores. It is MIT and lives in
`Freeboard.Core` (the domain model) and `Freeboard.Persistence` (the MySQL stores).

## Requirements
### Requirement: Machine asset domain model in Core

The system SHALL model an integration-agnostic asset in `Freeboard.Core` under
the MIT license. It SHALL define an `AssetKind` enumeration whose only value in
this increment is `Machine`, and an `AssetState` enumeration with values `Seen`
and `Retired`. It SHALL define machine-identity derivation that is integration
agnostic: given an observed hardware serial and host uuid, identity is the
hardware serial when the serial is usable, otherwise the host uuid when it is
usable, otherwise no identity. The identity is a `(kind, value)` pair where `kind`
is `Serial` or `HostUuid` and `value` is the normalized identifier. A serial SHALL
be normalized by trimming, collapsing internal whitespace, and upper-casing, and
SHALL be treated as unusable (as if absent) when the normalized value is empty or
matches a fixed, small deny-list of common blank or OEM-filler placeholder values
(for example `UNKNOWN`, `NONE`, `DEFAULT STRING`, `TO BE FILLED BY O.E.M.`). A
host uuid SHALL be normalized to its canonical form and SHALL be treated as
unusable when it does not parse as a uuid or is a firmware sentinel uuid (the
all-zero uuid or the all-ones uuid), which many unrelated machines report
identically. This code SHALL NOT reference `Freeboard.Enterprise` and SHALL add no
new package dependency.

#### Scenario: Machine kind exists

- **WHEN** the asset domain model is loaded
- **THEN** `AssetKind` has a `Machine` value and `AssetState` has `Seen` and
  `Retired` values

#### Scenario: Serial is the primary identity

- **WHEN** identity is derived from an observation with a non-blank hardware
  serial and any host uuid
- **THEN** the identity kind is `Serial` and the value is the normalized serial

#### Scenario: Host uuid is the fallback identity

- **WHEN** identity is derived from an observation with a blank or absent
  hardware serial and a non-blank host uuid
- **THEN** the identity kind is `HostUuid` and the value is the normalized uuid

#### Scenario: No identity when both are absent

- **WHEN** identity is derived from an observation with neither a usable hardware
  serial nor a usable host uuid
- **THEN** no identity is produced

#### Scenario: Placeholder serial is treated as missing

- **WHEN** identity is derived from an observation whose hardware serial is a
  placeholder value (blank, or a common OEM filler such as `To be filled by
  O.E.M.`) and whose host uuid is usable
- **THEN** the placeholder serial is ignored and the identity kind is `HostUuid`
  with the normalized uuid value

#### Scenario: Sentinel host uuid is treated as missing

- **WHEN** identity is derived from an observation with no usable serial and a
  host uuid that is a firmware sentinel (the all-zero uuid or the all-ones uuid)
- **THEN** the sentinel uuid is ignored and no identity is produced

### Requirement: Asset and asset-source schema and migration

The system SHALL persist assets in MySQL via a forward-only migration applied by
`freeboard system migrate`. The migration SHALL create an `asset` table (one row
per resolved machine) and an `asset_source` table (one row per reporting source
attachment). Every id and foreign-key column SHALL use `utf8mb4_bin` collation to
match the exact-byte id identity used elsewhere in the schema. The columns that
carry identity, enum-name, or source-key semantics - `kind`, `identity_kind`,
`identity_value`, `state`, `source`, and `external_id` - SHALL use a no-pad binary
collation (`utf8mb4_0900_bin`) so they compare by exact bytes; identity and source
uniqueness are therefore case-sensitive and whitespace-exact, treating values that
differ only in letter case (for example `fleetdm` and `FleetDM`) or in trailing
whitespace (for example `x` and `x `) as distinct rather than colliding. A padded
binary collation is insufficient because it would treat `x` and `x ` as equal. The `asset`
table SHALL carry the resolved `identity_kind` and `identity_value`, the `kind`,
the `state`, and seen/retired/created timestamps. The `asset_source` table SHALL
carry `source`, `external_id`, the observed serial and uuid, `asset_id`, and
seen/created timestamps. `organisation_id` SHALL be a scalar reference column
with no foreign key, following the evidence precedent. The internal
`asset_source` reference to `asset` SHALL be an enforced foreign key that binds
the organisation into the reference: `asset` SHALL carry a unique
`(id, organisation_id)` key and `asset_source` SHALL reference it by the composite
`(asset_id, organisation_id)` with `ON DELETE RESTRICT`, so the database itself
forbids an `asset_source` from pointing at an `asset` in another organisation.
These tables SHALL be mutable (no append-only triggers). The migration SHALL be
idempotent on replay. This code SHALL live in the MIT `Freeboard.Persistence`
project and SHALL NOT reference `Freeboard.Enterprise` or add any new dependency.

#### Scenario: Migration applies cleanly on a fresh database

- **WHEN** `freeboard system migrate` runs against a database with migrations
  001-016 applied
- **THEN** migration 017 applies successfully and the `asset` and `asset_source`
  tables exist

#### Scenario: Partial migration re-runs cleanly

- **WHEN** migration 017 is re-applied after a prior partial failure left some of
  its objects created
- **THEN** it completes without error because its table creation is idempotent
  (`CREATE TABLE IF NOT EXISTS`)

### Requirement: Assets are org-scoped with no cross-org leakage

The system SHALL scope every asset and asset-source to an organisation. An
asset's identity SHALL be unique per `(organisation_id, identity_kind,
identity_value)`, and an asset-source SHALL be unique per `(organisation_id,
source, external_id)`. These uniqueness comparisons SHALL be exact-byte and
case-sensitive, so two identities or two sources that differ only in letter case
remain distinct. Every store read SHALL filter by `organisation_id` so no read
returns an asset from another organisation. Cross-organisation isolation of the
internal `asset_source` -> `asset` reference SHALL NOT rest on query filtering
alone: the database SHALL enforce it, so an `asset_source` in one organisation
cannot reference an `asset` in another even if a caller supplies a mismatched id.

#### Scenario: Same identity in two organisations yields two assets

- **WHEN** two organisations each observe a machine with the same hardware serial
- **THEN** two distinct assets exist, one per organisation, and neither is
  returned when looking up the other organisation

#### Scenario: Lookup is filtered by organisation

- **WHEN** an asset is looked up by identity key or by `(source, external_id)`
  for an organisation that does not own it
- **THEN** no asset is returned

#### Scenario: A source cannot reference an asset in another organisation

- **WHEN** an `asset_source` row is written whose `organisation_id` differs from
  the `organisation_id` of the `asset` its `asset_id` names
- **THEN** the database rejects the write, so no `asset_source` ever points at an
  `asset` outside its own organisation

#### Scenario: Identity and source keys are case- and whitespace-exact

- **WHEN** two observations in one organisation differ only in the letter case of
  the source token or the identity value (for example `fleetdm` versus `FleetDM`),
  or only in trailing whitespace (for example `x` versus `x `)
- **THEN** they are treated as distinct keys rather than colliding on a
  case-insensitive or space-padded match

### Requirement: A source attaches to an asset by source and external id

The system SHALL let an integration attach to a machine as a source carrying its
own `(source, external_id)`. An asset SHALL be creatable from a source
observation and SHALL be lookupable by `(organisation_id, source, external_id)`
and by its identity key.

#### Scenario: Create and look up by source and external id

- **WHEN** a source observes a machine for an organisation with a serial or uuid
  and its own external id
- **THEN** a `Machine` asset is created, an `asset_source` row records the
  `(source, external_id)` pointing at it, and the asset is returned when looked
  up by `(organisation_id, source, external_id)`

#### Scenario: Look up by identity key

- **WHEN** the same machine is looked up by `(organisation_id, identity_kind,
  identity_value)`
- **THEN** the same asset is returned

### Requirement: Two sources reporting the same identity resolve to one machine

The system SHALL resolve two source observations that derive the same identity to
a single asset. The resolve-and-attach operation SHALL be atomic under
concurrency, using the org-scoped identity unique constraint, and SHALL preserve
the existing asset id so evidence attached to the machine stays resolvable. An
observation with no derivable identity SHALL be rejected without writing
anything.

#### Scenario: Two sources reporting the same serial

- **WHEN** one source (e.g. FleetDM) and a second source (e.g. an MDM) each
  observe a machine with the same hardware serial in the same organisation
- **THEN** one asset exists with two `asset_source` rows, and its id is unchanged
  between the two observations

#### Scenario: Two sources reporting the same uuid

- **WHEN** two sources each observe a machine with no serial but the same host
  uuid in the same organisation
- **THEN** one asset exists with identity kind `HostUuid` and two `asset_source`
  rows

#### Scenario: Observation with no identity is rejected

- **WHEN** a source observes a machine with neither a serial nor a host uuid
- **THEN** the write fails with a validation error and no asset or asset-source
  row is written

### Requirement: Conflicting resolution returns a conflict without moving data

The system SHALL NOT silently move data when an observation resolves ambiguously.
An existing `asset_source` for `(organisation_id, source, external_id)` that
already points at one asset SHALL NOT be silently relinked to a different asset;
when a new observation from that same `(source, external_id)` resolves to a
different asset, the write SHALL return a conflict and write nothing. Re-observing
the SAME asset (including a retired one) SHALL remain a normal update, not a
conflict. When an observation carries both a serial and a host uuid and the serial
resolves to one existing asset while the uuid independently resolves to a
different existing asset, the write SHALL return a conflict and SHALL NOT
auto-merge the two assets or silently pick one. Identity resolution stays
single-axis (serial primary, else host uuid); cross-axis auto-merge is out of
scope.

#### Scenario: A source is not silently relinked to a different asset

- **WHEN** a `(source, external_id)` that already points at one asset next
  observes a machine that resolves to a different asset
- **THEN** the write returns a conflict and neither the `asset_source` link nor
  any asset row is changed

#### Scenario: Serial and uuid resolving to two assets conflict

- **WHEN** an observation carries a serial that matches one existing asset and a
  host uuid that matches a different existing asset in the same organisation
- **THEN** the write returns a conflict and does not merge the two assets or pick
  one

### Requirement: Machine lifecycle is seen or retired as state, not delete

The system SHALL model a machine's lifecycle with the `state` column. A newly
observed machine SHALL be `Seen`. Retiring a machine SHALL be a state change that
sets `state` to `Retired` and records `retired_at`, leaving the asset and its
sources persisted; it SHALL NOT hard-delete the row. Re-observing a retired
machine SHALL return it to `Seen` and clear `retired_at`.

#### Scenario: Retiring is a state change

- **WHEN** a machine is retired
- **THEN** its `state` becomes `Retired` with a `retired_at` timestamp, and the
  asset row and its `asset_source` rows still exist

#### Scenario: Re-observing a retired machine reactivates it

- **WHEN** a retired machine is observed again by a source
- **THEN** its `state` returns to `Seen`, `retired_at` is cleared, and its id is
  unchanged

### Requirement: Read and write stores follow the evidence store split

The system SHALL expose asset persistence as a read store `IAssetStore` and a
write store `IAssetWriteStore` in `Freeboard.Persistence`, mirroring the
`IEvidenceStore`/`IEvidenceWriteStore` split and their DI registration. The read
store SHALL expose lookup by id, by identity key, and by `(source, external_id)`,
and SHALL expose no mutating method. The write store SHALL expose the
resolve-and-attach upsert and the retire state change, and SHALL be the only path
that changes asset state.

#### Scenario: Read store has no mutators

- **WHEN** the `IAssetStore` surface is inspected
- **THEN** it exposes only lookups (by id, by identity key, by source/external
  id) and no create, update, or delete method

#### Scenario: Write store performs upsert and retire

- **WHEN** the `IAssetWriteStore` surface is inspected
- **THEN** it exposes the resolve-and-attach upsert (returning a result that
  carries the resolved asset id and distinguishes created, updated, conflict, and
  invalid outcomes) and the retire operation, and these are the only ways asset
  state changes

