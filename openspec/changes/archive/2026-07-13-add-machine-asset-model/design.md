## Context

Freeboard has runtime evidence (`evidence_runs`, `evidence_checks`,
`attestation_responses` from migration `011_evidence.sql`) keyed on
`(organisation, requirement)`. There is no asset concept, so a per-machine
integration collector has nothing to attach machine-scoped evidence to. The
integration platform (FleetDM first, #52) needs a stable, integration-agnostic
`Machine` that multiple sources resolve to.

The existing evidence store establishes the pattern this design mirrors:

- Domain kinds live in `Freeboard.Core`; persistence stores enum values as their
  PascalCase names in `VARCHAR` columns (`kind`, `severity`, `result`).
- Read and write concerns are separate interfaces (`IEvidenceStore` read,
  `IEvidenceWriteStore` write) with separate DI registration
  (`AddEvidenceStore`, `AddEvidenceWriteStore`), both `TryAdd`-ing the shared
  `IUlidFactory`.
- Ids are ULIDs in `CHAR(26)` `utf8mb4_bin` columns; `organisation_id` is a
  scalar `VARCHAR(190) utf8mb4_bin` reference with NO foreign key (survives
  organisation deletion / gitops churn), following the `authz_audit_events`
  precedent. Internal references between the new tables ARE enforced as FKs.
- Migrations are embedded `Migrations/NNN_slug.sql` resources, ordered by
  ordinal, applied by `freeboard system migrate`, and must be idempotent on
  replay. The highest current ordinal is 016, so this change adds 017.
- The write store uses plain SQL and maps a MySQL duplicate-key error to a
  conflict result.

Key difference from evidence: assets are mutable. An asset's `state`,
`last_seen_at`, and `hostname` change over time. So the asset tables have no
append-only triggers, and the write store uses upsert semantics rather than
insert-only. Unlike evidence, an observation can resolve to a conflict (see the
conflict-semantics decision), so the upsert returns a richer result than the
evidence store's `WriteResult`.

Constraints: MIT only, in `Freeboard.Core` and `Freeboard.Persistence`; nothing
in `Freeboard.Enterprise`. Reference graph unchanged (`Persistence -> Core`). No
new package dependencies.

## Plan synthesis

This change unifies two independently drafted plans (a Planner draft and a Codex
draft) that agreed on the core shape - Core domain primitives, a Persistence
read/write store split mirroring the evidence store, `asset` + `asset_source`
tables, org-scoped unique keys, ULID `CHAR(26)` ids, a scalar no-FK
`organisation_id`, retire-as-state-change, on-observe reactivation, and migration
017. The decisions below note, where the two diverged, which source each resolved
idea came from and why it was chosen. The through-line is: prefer the safer
behaviour under concurrent collectors, and keep the surface minimal.

## Goals / Non-Goals

**Goals:**

- A `Machine` asset can be created and looked up by `(source, external_id)` and
  by its identity key.
- Two sources reporting the same serial (or the same uuid when neither has a
  serial) resolve to one `Machine`.
- Assets are org-scoped with no cross-org leakage.
- Retiring a machine is a state change, not a hard delete.
- Read/write store split matching the evidence store convention.

**Non-Goals:**

- Any collector, ingest endpoint, discovery runner, UI, or CLI (separate
  changes).
- Linking evidence rows to a machine (T3).
- Merging a serial-identified asset with a uuid-identified asset for the same
  physical machine (see Open Questions).
- Asset kinds beyond `Machine`, and inventory attributes beyond identity plus a
  display hostname.

## Decisions

### Decision: identity key is a single canonical `(kind, value)` on the asset

Each asset stores its resolved identity as two columns: `identity_kind`
(`Serial` or `HostUuid`) and `identity_value` (the normalized identifier).
Derivation, in `Freeboard.Core`, is: if a usable hardware serial is present,
identity is `(Serial, normalized-serial)`; else if a usable host uuid is present,
identity is `(HostUuid, normalized-uuid)`; else there is no stable identity and
the observation is rejected.

Normalization and the "usable" test:

- Serial: trim, collapse internal whitespace runs to a single space, and
  upper-case (invariant). The serial is unusable when the normalized value is
  empty or matches a small placeholder deny-list of common blank/OEM filler
  values. The deny-list is a fixed in-code constant (not configuration): `UNKNOWN`,
  `NONE`, `NULL`, `N/A`, `NA`, `DEFAULT STRING`, `TO BE FILLED BY O.E.M.`,
  `SYSTEM SERIAL NUMBER`, `0`. Comparison is against the normalized (trimmed,
  whitespace-collapsed, upper-cased) value.
- Host uuid: parse as a `Guid`; the value is unusable when it does not parse. The
  normalized value is the canonical lower-case hyphenated (`D`) form, so `{...}`,
  brace, and case variants of one uuid collapse to a single identity.

Placeholder rejection is a correctness requirement, adopted from the Codex plan
(its H3): several unrelated machines commonly report a blank or identical BIOS
filler serial (for example `To be filled by O.E.M.`). Without rejecting those,
they would all normalize to one `identity_value` and collapse into a single
asset - silently merging distinct machines. When the serial is a placeholder the
derivation falls through to the host uuid, exactly as if the serial were absent.
The Planner plan only trimmed and case-folded; that is insufficient. The deny-list
is deliberately short and documented rather than an elaborate configurable filter
(code-as-liability): it covers the values seen in practice and can be extended in
a later change if a real collector reports another.

Rationale: a single canonical identity column pair gives one org-scoped unique
constraint to dedup on, keeps the store logic simple, and lets both the write
store and future Core-referencing collectors compute identity the same way. The
derivation lives in Core (not Persistence) because it is integration-agnostic
domain logic reused by the FleetDM collector and the discovery runner, which
reference `Core` but not `Persistence`.

Alternatives considered:

- Cross-axis match (dedup on serial OR uuid across both columns). Rejected for
  this increment: it needs a two-column match with tie-break rules for when two
  existing assets collide (one by serial, one by uuid), which is real
  complexity for a case we have no collector to exercise yet. Deferred; see Open
  Questions. The single-canonical model does not preclude adding it later.
- Storing identity only implicitly (no columns, recompute per query). Rejected:
  the unique constraint needs concrete columns.

### Decision: raw observed serial/uuid live on `asset_source`, resolved identity on `asset`

`asset` holds only the resolved canonical identity (`identity_kind`,
`identity_value`); `asset_source` records what each source actually observed
(`observed_serial`, `observed_host_uuid`) plus its `(source, external_id)`. This
keeps per-source raw observations auditable and separate from the machine's
resolved identity, and mirrors the evidence split of run-identity vs per-check
detail.

This resolves divergence 5. The Planner plan put the raw values on
`asset_source`; the Codex plan additionally carried `hardware_serial` and
`host_uuid` columns on `asset` with their own org-scoped unique keys. The
per-source placement (Planner) wins because `asset_source` is the faithful record
of what THIS source reported, which can legitimately differ from the resolved
identity, and because the asset-level serial/uuid columns and their unique keys
only exist to support cross-axis dedup - a non-goal here (see divergence 3).
Adding them would encode a dedup axis the store does not use. The cross-axis
conflict guard needs no asset-level uuid column: it looks an existing uuid-only
asset up by its canonical identity (`identity_kind = HostUuid`), which is already
indexed.

### Decision: atomic upsert via a short locking transaction, unique constraint as backstop

This resolves divergence 4. The Planner plan used a single
`INSERT ... ON DUPLICATE KEY UPDATE`. The Codex plan used a transaction with
`SELECT ... FOR UPDATE` and explicit conflict detection. Codex's mechanism wins,
because the conflict cases in the conflict-semantics decision below (a source
relinking to a different asset; a serial and uuid resolving to two different
existing assets) cannot be detected by a bare `ON DUPLICATE KEY UPDATE` - that
statement blindly updates the matched row and cannot refuse. Correctness under
concurrent collectors is the priority, so the store reads candidates under a lock,
decides insert vs update vs conflict, and relies on the org-scoped unique
constraints as the race backstop.

The transaction runs at `READ COMMITTED`, not the MySQL default `REPEATABLE READ`.
This is deliberate and load-bearing for the first-observation race. Under
`REPEATABLE READ`, a `SELECT ... FOR UPDATE` against a not-yet-existing
`(organisation_id, identity_kind, identity_value)` or `(organisation_id, source,
external_id)` row takes InnoDB gap and insert-intention locks; two concurrent
first-observations of the same identity would then deadlock (error 1213) or
lock-wait-timeout (1205) on those gap locks BEFORE either reaches the duplicate-key
insert, and the victim would throw instead of resolving. `READ COMMITTED` disables
gap locks, so a `FOR UPDATE` on a missing row takes no lock and both transactions
proceed to the insert; the loser then fails with a plain duplicate-key error
(1062), which the single retry already handles by taking the update path. Setting
per-transaction isolation is an established pattern here: `MySqlAuthRateLimitStore`
opens its transaction with `IsolationLevel.ReadCommitted` for the same reason -
serialize on real rows, avoid gap-lock contention on seeded/first-touch rows.

`UpsertMachineFromSourceAsync` runs in one transaction and returns an
`AssetUpsertResult` (see the store-split decision). The algorithm, retried at most
once on a duplicate-key race:

1. Validate the observation and derive `(identity_kind, identity_value)` in Core.
   No identity -> return `Invalid`, write nothing.
2. Open a connection and `BEGIN` at `READ COMMITTED` (so a `FOR UPDATE` on a
   not-yet-existing identity or source row takes no gap lock; see above).
3. `SELECT ... FOR UPDATE` the `asset_source` row for
   `(organisation_id, source, external_id)`. Record its `asset_id` if present
   (`existingSourceAssetId`).
4. `SELECT ... FOR UPDATE` the canonical asset by
   `(organisation_id, identity_kind, identity_value)` (`canonicalAsset`, may be
   null). When the observation carries BOTH a usable serial and a usable uuid and
   the canonical identity is `Serial`, also `SELECT ... FOR UPDATE` the asset with
   `(organisation_id, HostUuid, normalized-uuid)` (`uuidAsset`, may be null).
5. Cross-axis conflict: if `canonicalAsset` and `uuidAsset` are both present and
   differ, `ROLLBACK` and return `Conflict` (see divergence 3). Do not merge, do
   not pick one.
6. Target asset:
   - `canonicalAsset` present -> target is it; `UPDATE` it: `last_seen_at = @Now`,
     `state = 'Seen'`, `retired_at = NULL`, `hostname = COALESCE(@Hostname,
     hostname)`. The `id` is never touched, so an existing machine keeps its id
     and any attached evidence stays resolvable.
   - else `INSERT` a new asset with a fresh ULID. A duplicate-key error here means
     a concurrent transaction inserted the same identity first: `ROLLBACK` and
     restart the algorithm once, which now takes the update path.
7. Source relink guard: if `existingSourceAssetId` is present and differs from the
   target id, `ROLLBACK` and return `Conflict` (see divergence 2). Never silently
   move an existing `(source, external_id)` to a different asset.
8. Attach the source:
   - `existingSourceAssetId` equals the target id -> `UPDATE` the `asset_source`:
     `last_seen_at = @Now`, `observed_serial`, `observed_host_uuid`.
   - no existing source -> `INSERT` a new `asset_source` pointing at the target. A
     duplicate-key error here is the same race as step 6; restart once.
9. `COMMIT`. Return `Created` (asset inserted) or `Updated` (asset matched) with
   the target id.

The `FOR UPDATE` locks serialize concurrent observations of the same identity or
the same source once the row exists. For a first-time insert there is no row to
lock; under `READ COMMITTED` the `FOR UPDATE` on the missing row takes no gap
lock, so the org-scoped unique constraint plus the single retry is the race
backstop: the loser's insert fails with a duplicate-key error (MySQL 1062) and the
retry resolves to an update. The retry set is exactly 1062 (duplicate key) -
because `READ COMMITTED` removes the gap locks, the first-observation race cannot
surface as a 1213 deadlock or 1205 lock-wait-timeout, so those are not swallowed as
retryable. `ON DUPLICATE KEY UPDATE` is not used because it cannot express the
conflict refusals; the asset tables have no append-only triggers, so a plain
`UPDATE` is allowed (unlike the evidence store).

Rationale: correctness under concurrent collectors. Preserving the id on update is
the invariant that makes dedup meaningful; the lock-read-decide shape is what lets
the store refuse a data-corrupting relink instead of performing it.

### Decision: conflicting resolution returns a conflict, never silent data movement

This resolves divergences 2 and 3 toward the safer position; both rules come from
the Codex plan (its H4 and M1). An observation resolves to a `Conflict` result -
writing nothing - in two cases:

- Source relink (divergence 2): an existing `asset_source` for
  `(organisation_id, source, external_id)` already points at asset X, and the new
  observation resolves to a different asset Y. Silently re-pointing the source
  would send that source's future evidence to the wrong machine, so the store
  refuses. The Planner plan treated a reimage/serial-churn re-point as acceptable;
  that is unsafe and is not adopted. The distinction that keeps normal operation
  working: re-observing the SAME asset (the source still resolves to X) is a
  normal update, including reactivating a retired machine - only a move to a
  DIFFERENT asset is a conflict.
- Cross-axis collision (divergence 3): the observation carries both a serial and a
  uuid, the serial resolves to existing asset X, and the uuid independently
  resolves to a different existing asset Y. The store returns a conflict rather
  than auto-merging X and Y or silently picking one.

Identity remains single-axis (serial primary, else uuid): the store resolves and
upserts on the canonical axis only, so it does NOT promote a uuid-only asset when
a serial later appears, and it does NOT record a serial-identified asset's uuid as
a second identity. Cross-axis auto-merge is an explicit non-goal (see Open
Questions). The consequence is that a serial-only source and a uuid-only source
for the same physical machine still create two assets when only one axis has an
existing asset; the conflict guard fires only when BOTH axes already resolve to
two distinct existing assets, which is the case that would otherwise corrupt data.

### Decision: seen/retired is a `state` column; retirement is an UPDATE

`asset.state` is `Seen` or `Retired`, defaulting to `Seen`. `RetireAsync` sets
`state = 'Retired'` and `retired_at = @Now`; the row and its sources persist, so
any evidence attached later still resolves. Re-observing a retired machine
(step 2 above) sets `state = 'Seen'` and clears `retired_at`, because a machine
that reports again is live. Retirement is owned by the discovery runner (T4),
not by this change; the store only exposes the state transition.

Rationale: acceptance requires retirement to be a state change, not a delete;
the append-only history argument does not apply (assets are mutable), so a plain
`state` column is the least-liability model.

### Decision: read/write store split mirroring evidence

- `IAssetStore` (read): `GetByIdAsync(org, assetId)`,
  `GetByIdentityAsync(org, kind, value)`, `GetBySourceAsync(org, source,
  externalId)`. All take and filter by `organisation_id`, so no query can cross
  orgs. `GetByIdAsync` carries `organisation_id` too so the no-cross-org invariant
  holds for by-id reads: a by-id read for the wrong org returns nothing.
- `IAssetWriteStore` (write): `UpsertMachineFromSourceAsync(observation)` and
  `RetireAsync(org, assetId)`. The read interface exposes no mutators; the write
  interface is the only path that changes state.

The upsert returns an `AssetUpsertResult`, not the shared `WriteResult`, because
it must carry the resolved asset id and distinguish four outcomes: `Created`,
`Updated`, `Conflict`, and `Invalid`. `WriteResult` (`Error`, `IsConflict`) cannot
carry the id and cannot express created-vs-updated, so a dedicated record is
warranted:

```csharp
public enum AssetUpsertStatus { Created, Updated, Conflict, Invalid }

public sealed record AssetUpsertResult(AssetUpsertStatus Status, string? AssetId, string? Error);
```

`RetireAsync` returns the shared `WriteResult` (it has no id to return and only
succeeds, no-ops, or fails).

The read surface is the minimum to satisfy the acceptance criteria (lookup by
`(source, external_id)` and by identity key). `GetByIdAsync(org, assetId)` is the
minimal by-id read the store owes for future evidence resolution; the write path
resolves in its own transaction and does not call it. Listing/enumeration for the
discovery runner and UI is deliberately omitted until those consumers exist
(code-as-liability).

### Decision: schema

`asset` table:

- `id CHAR(26) utf8mb4_bin NOT NULL` PK (ULID).
- `organisation_id VARCHAR(190) utf8mb4_bin NOT NULL` (scalar ref, no FK).
- `kind VARCHAR(32) utf8mb4_bin NOT NULL` (AssetKind name, `Machine`).
- `identity_kind VARCHAR(16) utf8mb4_bin NOT NULL` (`Serial`/`HostUuid`).
- `identity_value VARCHAR(190) utf8mb4_bin NOT NULL`.
- `hostname VARCHAR(255) NULL` (display only).
- `state VARCHAR(16) utf8mb4_bin NOT NULL` (`Seen`/`Retired`).
- `first_seen_at DATETIME(6) NOT NULL`, `last_seen_at DATETIME(6) NOT NULL`,
  `retired_at DATETIME(6) NULL`, `created_at DATETIME(6) NOT NULL`.
- `UNIQUE (organisation_id, identity_kind, identity_value)` - org-scoped dedup.
- `UNIQUE (id, organisation_id)` - lets `asset_source` reference asset by a
  composite `(asset_id, organisation_id)` FK, binding org into the internal
  reference so a source can never point at an asset in another org
  (defense-in-depth for the no-cross-org acceptance criterion; cross-org
  isolation does not rest solely on query filtering).

`asset_source` table:

- `id CHAR(26) utf8mb4_bin NOT NULL` PK (ULID).
- `asset_id CHAR(26) utf8mb4_bin NOT NULL`.
- `organisation_id VARCHAR(190) utf8mb4_bin NOT NULL` (denormalized so the
  uniqueness and scoping are org-local without a join).
- `source VARCHAR(64) utf8mb4_bin NOT NULL` (integration token, e.g. `fleetdm`).
  Binary collation so the source token dedups by exact case-sensitive bytes:
  without it MySQL 8's case-insensitive default would collide `fleetdm` and
  `FleetDM` inside `UNIQUE (organisation_id, source, external_id)`, matching how
  `011_evidence.sql` binary-collates its key columns.
- `external_id VARCHAR(190) utf8mb4_bin NOT NULL`.
- `observed_serial VARCHAR(190) NULL`, `observed_host_uuid VARCHAR(190) NULL`.
- `first_seen_at DATETIME(6) NOT NULL`, `last_seen_at DATETIME(6) NOT NULL`,
  `created_at DATETIME(6) NOT NULL`.
- `UNIQUE (organisation_id, source, external_id)` - a source's external id maps
  to exactly one machine per org.
- `CONSTRAINT fk_asset_source_asset FOREIGN KEY (asset_id, organisation_id)
  REFERENCES asset (id, organisation_id) ON DELETE RESTRICT` - composite internal
  ref that binds `organisation_id`, so an `asset_source` in org A cannot reference
  an `asset` in org B. Enforced like the evidence precedent, widened to carry org.
  InnoDB maintains an index on `(asset_id, organisation_id)` for this FK, whose
  leftmost `asset_id` prefix already serves the asset-id reverse lookup, so no
  separate `KEY (asset_id)` is added.

Rejected Codex schema additions: asset-level `hardware_serial`/`host_uuid` columns
and their `uq_asset_org_kind_serial`/`uq_asset_org_kind_host_uuid` unique keys
(they only serve the deferred cross-axis dedup; raw observed values live on
`asset_source` instead), and an `updated_at` column on both tables (redundant with
`last_seen_at`, which every mutation refreshes). Dropping them keeps the schema to
what the acceptance criteria need (code-as-liability).

Adopted (previously considered droppable): the composite `(id, organisation_id)`
unique key on `asset` plus the composite `(asset_id, organisation_id)`
`asset_source` FK. A single-column `asset_source.asset_id -> asset(id)` FK does not
bind `organisation_id`, so it would let an `asset_source` scoped to org A reference
an `asset` in org B; the no-cross-org acceptance criterion must not rest solely on
query double-filtering. The composite key/FK make the database enforce org
containment of the internal reference. The extra unique key is cheap and the guard
is defense-in-depth, so it is kept, not dropped.

Dropped speculative index: `KEY (organisation_id, state)` (list-by-org-and-state).
It serves only the future discovery runner (T4), which is a non-goal here, so under
code-as-liability it is not added now - T4 will add the exact index its query needs
in a trivial later migration.

Org scoping: every uniqueness constraint and every store query includes
`organisation_id`, so the same identity or the same `(source, external_id)` under
two orgs yields two distinct assets and no read crosses an org boundary.

The migration is idempotent (`CREATE TABLE IF NOT EXISTS`), forward-only, and
adds no triggers (assets are mutable).

## Risks / Trade-offs

- [Cross-axis identity split] A source reporting only a serial and another
  reporting only a uuid for the same physical machine create two assets. ->
  Mitigation: FleetDM (the first and only near-term source) reports a hardware
  serial, so both sources will normally derive `Serial`. Documented as a
  non-goal and open question; the canonical-identity model can be extended
  without a data migration if a real need appears. When both axes already resolve
  to two different assets, the store returns a conflict rather than corrupting
  data.
- [Serial churn / reimage re-points a source] If an existing
  `(source, external_id)` later resolves to a different asset, the store returns a
  `Conflict` and writes nothing, rather than silently moving the source (which
  would send its future evidence to the wrong machine). -> Trade-off: a genuine
  reimage that changes a machine's serial needs an explicit re-link path (a future
  change owns that); for v1 the safe refusal is preferred over silent movement.
  Re-observing the same asset (including reactivating a retired one) is unaffected.
- [Missing/placeholder identity] An observation with neither a usable serial nor a
  usable uuid cannot dedup. -> Mitigation: reject with `Invalid`, writing nothing,
  rather than creating an unmergeable asset. A placeholder serial (blank or common
  OEM filler) is treated as missing so unrelated machines do not collapse into one
  asset.
- [Mutable rows vs evidence's append-only guarantee] Assets can be updated, so
  they lack the tamper-evidence evidence has. -> Mitigation: assets are
  discovered runtime state (identity + last-seen), not attested compliance
  evidence; the evidence that references a machine keeps its own append-only
  guarantee.

## Migration Plan

- Forward-only: add `017_assets.sql`. Applied by `freeboard system migrate`
  after 016. No backfill (no prior asset data). Idempotent on replay via
  `CREATE TABLE IF NOT EXISTS`.
- Rollback: none required pre-release; the tables are additive and unused by
  existing code. If ever needed, a later forward migration would drop them (DDL
  drop is unaffected by the absence of triggers).

## Open Questions

- Cross-axis dedup (match on serial OR uuid) is deferred. Open tension for
  reviewers: v1 keeps identity single-axis, so a serial-only source and a
  uuid-only source for the same machine produce two assets when only one axis has
  a pre-existing asset; the store only refuses (conflict) when both axes already
  resolve to two distinct assets. Is the residual duplication acceptable until a
  real cross-axis source exists, or should promotion/merge land now? Current plan:
  defer, and revisit when a source that omits the serial ships.
- Conflict-return semantics for reviewers: a source relink and a cross-axis
  collision both return `Conflict` and write nothing. This is safe but silent to
  the collector - there is no operator surface yet to see or resolve a conflicted
  observation (the ingest/collector change owns that). Confirm returning a
  conflict result the caller can log is enough for v1.
- Serial case-fold: identity upper-cases the serial. Confirm no real-world serial
  is case-sensitively distinct in a way that matters; if one is, drop the
  case-fold. Low risk for FleetDM.
- `source` token vocabulary (free string vs enum). Left as a free `VARCHAR(64)`
  token for now; the FleetDM change can standardize it.
