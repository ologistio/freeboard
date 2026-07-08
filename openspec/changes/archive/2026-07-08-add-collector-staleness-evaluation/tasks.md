## 1. feat(core): collector cadence policy and staleness evaluation

- [x] 1.1 Add `EvidenceCollectorFrequency` in `src/Freeboard.Core/GitOps` owning the
  canonical frequency token set and a cadence-to-(window, grace) map (`continuous`
  1h/15m, `daily` 1d/6h, `weekly` 7d/1d, `monthly` 31d/3d, `quarterly` 92d/7d, `annual`
  366d/30d), plus `IsStale(DateTime collectedAtUtc, string? frequency, DateTime nowUtc)`
  returning false for null/blank/unknown cadence and otherwise
  `nowUtc - collectedAtUtc > window + grace`.
- [x] 1.2 Point `ConfigValidator`'s frequency-token check at the shared token set,
  removing its private `FrequencyTokens` copy; keep the existing diagnostic message.
- [x] 1.3 Add `Freeboard.Core` unit tests for each cadence's window-plus-grace boundary
  (just-inside vs just-over), the sub-day `continuous` window, and null/blank/unknown
  cadence never stale.

## 2. feat(persistence): record collector identity and cadence on runs

- [x] 2.1 Add forward-only migration `015_evidence_collector_identity.sql` as a single
  additive `ALTER TABLE evidence_runs` adding nullable `collector_id VARCHAR(190)`
  (`utf8mb4_bin`) and `frequency VARCHAR(16)`, matching the repo idiom (migrations 005,
  012 use bare `ADD COLUMN`). No foreign key on `collector_id`. No new index (D5: the
  existing `ix_evidence_runs_org_requirement_collected` `(organisation_id,
  requirement_id, collected_at)` already serves the batch read via its leftmost
  `(organisation_id, requirement_id)` prefix; per-collector grouping is done in the read
  store after the fetch, so a fourth index only adds write cost on the append-hot table).
  No backfill and no touching of `trg_evidence_runs_no_update`: the append-only guard is
  preserved, and pre-migration rows read back null on both columns (attributed by the
  `collector_ref` fallback, never stale for lack of a cadence - D1). The runner is not
  transactionally atomic with the `schema_migrations` insert and plain `ADD COLUMN` is
  not idempotent, so 015 is NOT atomically replay-safe (D5): a crash after the
  `ALTER` commits but before the version is recorded makes a re-run fail on the duplicate
  column. Document the operational recovery in the migration header: drop the added
  columns and re-run `freeboard system migrate`, or record the `schema_migrations` row
  for version `015` by hand.
- [x] 2.2 Add trailing-optional nullable `CollectorId` and `Frequency` (both defaulting
  to `null`) as the LAST parameters of the `NewEvidenceRun` record (`IEvidenceWriteStore.cs`),
  so the sole existing call site (`EvidenceIngestEndpoints.cs`) and any test constructors
  compile unchanged; insert both in `MySqlEvidenceWriteStore.AppendAsync` (attestation
  appends leave both at the null default).
- [x] 2.3 Add nullable `CollectorId` and `Frequency` to `EvidenceRunRow`
  (`EvidenceReadModels.cs`) and include the columns in `MySqlEvidenceStore.RunColumns`
  and `RunScalar`.

## 3. feat(persistence): per-collector status derivation with Stale

- [x] 3.1 Replace `AssessmentResultRow` with
  `CollectorEvidenceStatusRow(OrganisationId, RequirementId, CollectorId, Status,
  LastCollectedAt)` in `EvidenceReadModels.cs`, adding `Stale` to the status vocabulary
  doc; replace `GetAssessmentResultsAsync` on `IEvidenceStore` with
  `GetCollectorEvidenceStatusesAsync(IReadOnlyCollection<string> organisationIds, ...)`.
- [x] 3.2 Implement the batch read in `MySqlEvidenceStore`: read collector-kind runs for
  the given organisations, compute the effective collector id (`collector_id` else the
  `collector_ref` prefix before `:` when present), group by
  `(organisation, requirement, collector)`, pin each group's latest run by the existing
  order, fetch its checks, and derive status with precedence
  `HardFailure > Stale > SoftFailure > Passing`, using
  `EvidenceCollectorFrequency.IsStale` against `timeProvider.GetUtcNow().UtcDateTime`.
  Keep the single `RepeatableRead` snapshot and read only evidence tables.
- [x] 3.3 Inject `TimeProvider` into `MySqlEvidenceStore`; in
  `PersistenceServiceCollectionExtensions.AddEvidenceStore` add
  `services.TryAddSingleton(TimeProvider.System)`. Blast radius of the new ctor param:
  10 bare `new MySqlEvidenceStore(db.ConnectionFactory)` construction sites in
  `tests/Freeboard.Persistence.Tests/EvidenceIntegrationTests.cs` (lines 105, 130, 155,
  173, 192, 259, 276, 308, 353, 368) plus the single DI registration at
  `PersistenceServiceCollectionExtensions.cs:64`. Make the `TimeProvider` param
  trailing with a `TimeProvider.System` default so the 10 test sites and DI keep
  compiling; the staleness integration cases pass an explicit `TimeProvider` (or use a
  far-past `collected_at`) where they assert stale-vs-fresh.

## 4. feat(web): record cadence at ingest and surface status in the SoA

- [x] 4.1 In `EvidenceIngestEndpoints.IngestAsync`, pass `CollectorId =
  validated.CollectorId` and `Frequency = string.IsNullOrWhiteSpace(collector.Frequency)
  ? null : collector.Frequency` into `NewEvidenceRun`; leave the `freeboard.evidence.v1`
  validation and payload contract unchanged.
- [x] 4.2 Register the read store: add `AddEvidenceStore(freeboardConnectionString)` in
  `src/Freeboard/Program.cs`.
- [x] 4.3 Inject `IEvidenceStore` into `StatementOfApplicabilityModel`; after building
  `Nodes`, batch-read `GetCollectorEvidenceStatusesAsync` for the in-scope node ids inside
  the existing store-failure try/catch, and expose a lookup keyed by
  `(organisationId, requirementId, collectorId)` that returns `Unknown` when absent.
- [x] 4.4 In `StatementOfApplicability.cshtml`, render a status badge on each collector
  check, `Stale` as "collection stopped" distinct from `Unknown` "not collected", with
  `Passing`/`SoftFailure`/`HardFailure` distinct. `ResolveDrilldown` and `SoaCheckNode`
  stay unchanged.

## 5. test: staleness coverage

- [x] 5.1 Update `EvidenceIntegrationTests.cs` for the new columns and the per-collector
  read: a fresh passing collector, an overdue passing collector downgraded to `Stale`, a
  stale-and-hard-failing collector staying `HardFailure`, a null-cadence run never
  `Stale`, a legacy null-`collector_id` run attributed by its `collector_ref` prefix so
  its verdict still shows (and, having null cadence, never `Stale`), and two collectors on
  one requirement where the stale one is not hidden by the fresh one. Cases skip cleanly
  without `FREEBOARD_TEST_DB`.
- [x] 5.2 Add an additive-migration integration case: against a database with the
  evidence migration applied, insert a legacy collector run (null
  `collector_id`/`frequency`, `collector_ref` = `collector_id:run_id`), run migration
  `015`, and assert the `evidence_runs` table gains the nullable `collector_id` and
  `frequency` columns while the legacy run stays null on both (no backfill) and its
  `result`/`collected_at`/`collector_ref`/checks are unchanged. Skips without
  `FREEBOARD_TEST_DB`.
- [x] 5.3 Add a `FakeEvidenceStore` read double implementing `IEvidenceStore` in
  `tests/Freeboard.Web.Tests/FakeEvidenceStores.cs`, returning canned per-collector
  statuses. It SHALL derive the returned status set the same way the real store does:
  latest run per `(organisation, requirement, collector)` with the shared staleness rule
  applied, not a full-history dump, so `Stale`-vs-`Unknown` render tests exercise
  realistic store output. Expose it on `AuthWebFactory` (a property beside the existing
  `EvidenceStore` write double) and register it in `ConfigureTestServices`
  (`RemoveAll<IEvidenceStore>()` then `AddSingleton<IEvidenceStore>(...)`), because
  `Program.cs` now registers the real `MySqlEvidenceStore` and an authenticated SoA
  render would otherwise hit an unreachable store.
- [x] 5.4 Add web page test coverage in `StatementOfApplicabilityPageTests.cs` proving a
  collector check renders `Stale` ("collection stopped") and `Unknown` ("not collected")
  distinctly, via the `FakeEvidenceStore` read double.

## 6. Verification

- [x] 6.1 Run `dotnet build` for the solution.
- [x] 6.2 Run `dotnet test` (unit/web tiers green with no external deps; MySQL
  integration cases skip without `FREEBOARD_TEST_DB`).
- [x] 6.3 Run `openspec validate "add-collector-staleness-evaluation" --strict` and fix
  any errors.
