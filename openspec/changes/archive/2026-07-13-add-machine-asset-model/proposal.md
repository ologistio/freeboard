## Why

Building any real integration collector (starting with FleetDM, #52) needs a
first-class asset to attach per-machine evidence to. No `Asset`/`AssetKind`
concept exists in the codebase today. The asset must be integration-agnostic: one
laptop seen by FleetDM now and by an MDM/EDR later must resolve to a single
`Machine`, not a duplicate per source. Identity lives on the asset; each
integration attaches as a source with its own external id.

## What Changes

- Add an `AssetKind` enum with a `Machine` kind, an `AssetState` enum
  (`Seen`/`Retired`), and integration-agnostic machine-identity derivation
  (hardware serial primary, host uuid fallback) in `Freeboard.Core`. Serials are
  normalized and common blank/OEM-filler placeholder serials are treated as
  missing so unrelated machines do not collapse into one asset.
- Add `asset` and `asset_source` tables in `Freeboard.Persistence` via a new
  forward-only migration (`017_assets.sql`). Unlike evidence, these tables are
  mutable (lifecycle and last-seen change over time), not append-only.
- Dedup identity is org-scoped: an asset is unique per
  `(organisation, identity_kind, identity_value)`. `asset_source` carries
  `(source, external_id)`, unique per organisation, so a future integration
  attaches to the same machine.
- Add a read store `IAssetStore` and a write store `IAssetWriteStore` following
  the existing `IEvidenceStore`/`IEvidenceWriteStore` split. The write store
  resolves an observation to one machine and attaches the reporting source in one
  short locking transaction, and retires a machine as a state change (not a
  delete). It refuses rather than corrupts data: it returns a conflict instead of
  silently relinking an existing `(source, external_id)` to a different asset, or
  when a serial and a uuid resolve to two different existing assets.

## Capabilities

### New Capabilities

- `asset-model`: the integration-agnostic asset domain model and its MySQL
  persistence. Covers the `Machine` asset kind, org-scoped serial/uuid identity
  and dedup, per-source attachment by `(source, external_id)`, the seen/retired
  lifecycle, and the read/write store split.

### Modified Capabilities

None. This change adds a new capability and does not alter the requirements of
any existing spec. Evidence stays keyed on `(organisation, requirement)`;
attaching evidence per machine is a separate follow-up change (T3), out of scope
here.

## Impact

- New code in `Freeboard.Core` (`Assets/` domain types) and
  `Freeboard.Persistence` (store pair, MySQL implementations, migration
  `017_assets.sql`, DI registration). MIT only; nothing in `Freeboard.Enterprise`.
- No new package dependencies; reuses Dapper, MySqlConnector, and the existing
  `IUlidFactory` and migration runner.
- Unblocks the FleetDM collector (#52), the discovery runner (T4), and the
  evidence-per-machine change (T3).

## Non-goals

- No integration collector, HTTP ingest endpoint, or discovery runner. This
  change is the data model and store only; the FleetDM collector and discovery
  runner are separate changes that consume it.
- No web UI, CLI surface, or read API for assets.
- No cross-axis identity merge: a source reporting only a serial and another
  reporting only a uuid for the same physical machine are not merged in this
  increment (see design Open Questions).
- No change to the evidence tables or to how evidence is keyed. Linking evidence
  to a machine is deferred to T3.
- No hardware/software inventory attributes beyond what identity and display
  need; no speculative asset kinds beyond `Machine`.
