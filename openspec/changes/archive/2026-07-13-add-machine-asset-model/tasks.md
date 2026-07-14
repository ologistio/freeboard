## 1. Core domain model (feat(core): machine asset model)

- [x] 1.1 Add `src/Freeboard.Core/Assets/AssetKind.cs` with the `AssetKind` enum
  (`Machine`) and the `AssetState` enum (`Seen`, `Retired`).
- [x] 1.2 Add machine-identity derivation in `Freeboard.Core` (a small static
  helper and an identity value type, e.g. `MachineIdentity` with a
  `MachineIdentityKind` of `Serial`/`HostUuid`): serial primary, host uuid
  fallback, returning no identity when neither is usable. Normalize the serial
  (trim, collapse whitespace, upper-case) and reject it as unusable when empty or
  in a fixed, small placeholder deny-list (blank/OEM filler such as `UNKNOWN`,
  `NONE`, `DEFAULT STRING`, `TO BE FILLED BY O.E.M.`); the deny-list is an in-code
  constant, not configuration. Normalize the host uuid to its canonical form and
  reject it when it does not parse or is a firmware sentinel uuid (the all-zero
  uuid or the all-ones uuid), which many unrelated machines report identically.
  Keep it minimal (single responsibility, no speculative kinds).
- [x] 1.3 Add `Freeboard.Core.Tests` unit tests: assert `AssetKind` has `Machine`
  and `AssetState` has `Seen` and `Retired` (the "Machine kind exists" scenario);
  and for identity derivation: serial chosen when present, uuid fallback when
  serial blank, no identity when both unusable, serial normalization
  (whitespace/case) collapses to one value, a placeholder serial is treated as
  missing and falls through to the uuid, a sentinel host uuid (all-zero or
  all-ones) is treated as missing so an observation with only a placeholder serial
  and a sentinel uuid yields no identity, and uuid variants (`{...}`, brace, case)
  canonicalize to one value.

## 2. Persistence migration (feat(persistence): asset schema migration 017)

- [x] 2.1 Add `src/Freeboard.Persistence/Migrations/017_assets.sql` creating the
  `asset` and `asset_source` tables per design: `utf8mb4_bin` ids, and the no-pad
  binary collation `utf8mb4_0900_bin` on the exact-byte enum/token/key columns
  `identity_kind`, `identity_value`, `kind`, `state`, `source`, and `external_id`
  (so `fleetdm` and `FleetDM` do not collide, and `x` and `x ` do not collide as a
  padded `utf8mb4_bin` collation would, and identity values compare by exact bytes);
  org-scoped
  unique keys (`(organisation_id, identity_kind, identity_value)` and
  `(organisation_id, source, external_id)`); a `UNIQUE (id, organisation_id)` on
  `asset` and a composite `FOREIGN KEY (asset_id, organisation_id) REFERENCES asset
  (id, organisation_id) ON DELETE RESTRICT` on `asset_source` (binds org so no
  cross-org reference; its InnoDB index on `(asset_id, organisation_id)` also
  covers the asset-id reverse lookup, so no separate `KEY (asset_id)` is added);
  mutable tables (no append-only triggers); idempotent `CREATE TABLE IF NOT
  EXISTS`. Do not add
  `KEY (organisation_id, state)` - it serves only the future discovery runner (T4),
  which adds it when needed.
- [x] 2.2 Confirm the file is picked up as an embedded `Migrations/*.sql`
  resource (existing csproj glob) and orders after 016.

## 3. Store contracts and read models (feat(persistence): asset store interfaces)

- [x] 3.1 Add `IAssetStore` (read): `GetByIdAsync(org, assetId)`,
  `GetByIdentityAsync(org, identityKind, identityValue)`, `GetBySourceAsync(org,
  source, externalId)`, all `organisation_id`-filtered (including by-id), no
  mutators.
- [x] 3.2 Add `IAssetWriteStore` (write): `UpsertMachineFromSourceAsync`
  returning an `AssetUpsertResult`, and `RetireAsync(org, assetId)` returning the
  shared `WriteResult`.
- [x] 3.3 Add read models and inputs mirroring the evidence records:
  `AssetRow` (returned by the read store), a `NewMachineObservation` input, and an
  `AssetUpsertResult` (with an `AssetUpsertStatus` of
  `Created`/`Updated`/`Conflict`/`Invalid`) carrying the resolved asset id. No
  `AssetSourceRow` type: no read method returns a typed source row, so the
  integration test asserts `asset_source` rows via direct SQL instead of a mapped
  model (code-as-liability - add the type only when a consumer needs it).

## 4. MySQL implementations (feat(persistence): asset MySQL stores)

- [x] 4.1 Implement `MySqlAssetStore : IAssetStore` with hand-written Dapper
  reads filtered by `organisation_id`.
- [x] 4.2 Implement `MySqlAssetWriteStore : IAssetWriteStore` per the design
  algorithm, in one transaction opened at `IsolationLevel.ReadCommitted` (so a
  `FOR UPDATE` on a not-yet-existing identity/source row takes no gap lock and the
  first-observation race degrades to a plain 1062 duplicate-key error, as in
  `MySqlAuthRateLimitStore`): validate and derive identity, returning `Invalid`
  (writing nothing) when a required key is missing, a field exceeds its column
  width (length checked as Unicode runes), or no identity is derivable;
  `SELECT ... FOR UPDATE` the existing `asset_source` for
  `(organisation_id, source, external_id)` and the candidate asset(s) by identity;
  return `Conflict` when the serial and uuid resolve to two different existing
  assets (cross-axis) or when the existing source would relink to a different
  asset; otherwise update the matched asset (preserve id) or insert a new one, then
  update or insert the `asset_source` pointing at the resolved asset. Make both
  updates monotonic so a late-arriving older observation cannot regress state: gate
  the mutable fields on `@Now >= last_seen_at` (`state='Seen'`, clear `retired_at`,
  and the observed values only when the observation is at least as recent) and
  advance `last_seen_at` via `GREATEST(last_seen_at, @Now)`. Retry the resolution
  once on a duplicate-key race (the org-scoped unique constraint is the backstop).
  Implement `RetireAsync` as a `state`/`retired_at` UPDATE returning `WriteResult`,
  gated on `@Now >= last_seen_at` so a stale retirement is a no-op.
- [x] 4.3 Add DI extensions `AddAssetStore` and `AddAssetWriteStore` in
  `PersistenceServiceCollectionExtensions`, `TryAdd`-ing the shared
  `IUlidFactory`, mirroring the evidence registrations.

## 5. Integration tests (test(persistence): asset store integration)

- [x] 5.1 Add MySQL integration tests gated on `FREEBOARD_TEST_DB` (skip cleanly
  when unset), covering: migration creates tables/unique keys/FK; create and
  look up by `(source, external_id)` and by identity key; two sources with the
  same serial resolve to one asset with two sources and a stable id; two sources
  with the same uuid (no serial) resolve to one; same identity in two orgs yields
  two assets and no cross-org read; observation with no identity is rejected
  (`Invalid`); a placeholder-serial observation with a usable uuid resolves by
  uuid; retire is a state change (row persists); re-observe a retired machine
  returns it to `Seen`; an existing `(source, external_id)` resolving to a
  different asset returns `Conflict` and changes nothing; a serial and uuid
  matching two different existing assets returns `Conflict`; concurrent
  same-serial upserts resolve to one asset id (assert a single distinct id).
- [x] 5.2 Add a migration-017 idempotent-replay integration test (gated on
  `FREEBOARD_TEST_DB`), mirroring `EvidenceIntegrationTests.Migration011ReplayIsIdempotent`:
  migrate, then re-execute the raw `017_assets.sql` text a second time directly and
  assert it completes without error and both tables still exist. Covers the spec's
  "Partial migration re-runs cleanly" scenario (`CREATE TABLE IF NOT EXISTS`; no
  triggers to re-create).
- [x] 5.3 Add a `RetireAsync` no-op integration test case: retiring an unknown
  asset id, and retiring an id owned by a different org, each returns a no-op
  `WriteResult` (no rows changed) and throws no exception - covers the retire
  no-op/wrong-org path.
- [x] 5.4 Add two more MySQL integration tests (gated on `FREEBOARD_TEST_DB`, skip
  cleanly when unset):
  (a) a direct `INSERT` into `asset_source` whose `organisation_id` differs from
  the `organisation_id` of the `asset` its `asset_id` references is rejected by the
  composite `(asset_id, organisation_id)` foreign key (assert the raw insert
  throws); the store write path never produces this mismatch, so it must be
  exercised by direct SQL.
  (b) two observations in one org that differ only in the letter case of the source
  token (or of the identity value) - e.g. `fleetdm` versus `FleetDM` - resolve to
  two distinct rows, proving the org-scoped unique keys compare by exact bytes and
  do not collide case-insensitively.

## 6. Verification (chore: build and test)

- [x] 6.1 `dotnet build` the solution.
- [x] 6.2 `dotnet test` (unit tests pass; MySQL integration tests skip cleanly
  without `FREEBOARD_TEST_DB`, and pass when it is set against the test compose
  MySQL).
- [x] 6.3 Confirm `Freeboard.Architecture.Tests` still pass (no new
  `Freeboard.Enterprise` reference from Core or Persistence). Add a reflection
  assertion there that `IAssetStore` exposes only lookups and no mutating method
  (no create/update/delete/upsert/retire), covering the "Read store has no
  mutators" scenario.
- [x] 6.4 `openspec validate "add-machine-asset-model" --strict` passes.
