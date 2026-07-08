## Why

Evidence status is derived only from the latest run's checks, with no regard for how
old that run is or which collector produced it. A collector that stopped reporting
keeps its last `Passing` verdict forever, so an assessor sees a false green long after
collection died. Worse, when several collectors verify the same requirement, one
collector's fresh evidence hides another collector that has gone silent: the
per-requirement latest run is fresh even though a distinct collector stopped. The
system already records each collector's collection cadence (`frequency`), so it can
tell when a collector's latest evidence is overdue; it just does not act on it.

Issue #54 asks for a derived `stale` state, distinct from `unknown` (a collector that
never produced evidence), surfaced where an assessor can see it.

## What Changes

- Add a pure `Freeboard.Core` cadence policy (`EvidenceCollectorFrequency`) that maps
  a collector `frequency` token to a staleness window plus a cadence-scaled grace and
  decides `IsStale(collectedAtUtc, frequency, nowUtc)`. It takes `now` as a parameter
  so the rule is unit-testable without a clock, and it owns the canonical frequency
  vocabulary that `ConfigValidator` already validates (the token set is shared, not
  duplicated).
- Record the collecting collector's identity and cadence on each evidence run:
  first-class nullable `evidence_runs.collector_id` and `evidence_runs.frequency`
  columns, set at ingest from the authenticated collector's registration. The wire
  contract `freeboard.evidence.v1` is unchanged; the ingest endpoint already resolves
  the collector, so recording both is near-free. A purely additive forward-only migration
  adds the two nullable columns and nothing else. There is no backfill: pre-migration runs
  keep a null cadence and are never flagged stale, which preserves the `evidence_runs`
  append-only integrity guarantee (no trigger drop, no UPDATE of appended rows).
- Derive assessment status PER COLLECTOR, not per requirement. The read store groups
  each pair's runs by `(organisation, requirement, collector)` and evaluates each
  collector's latest run, so a stopped collector is never masked by another collector's
  fresh evidence for the same requirement. A pre-migration run with a null `collector_id`
  falls back to the `collector_id` prefix of the `collector_ref` idempotency key for
  identity (which ingest has always composed as `collector_id:run_id`), so its collector's
  last verdict is still attributed correctly; such a legacy run carries a null cadence and
  is never flagged stale.
- Add a `Stale` status to the string-typed status vocabulary and a batch read
  (`GetCollectorEvidenceStatusesAsync`) that returns per-collector statuses for a set
  of organisations in one call, replacing the per-requirement assessment projection
  (which had no production consumer and could mask a stopped collector).
- Surface the per-collector status on the Statement of Applicability page. Each
  collector check under a control shows its status; `Stale` renders as "collection
  stopped", distinct from `Unknown` (an expected collector that never produced
  evidence, derived by the page for a collector with no store row). The page
  batch-reads statuses for the visible organisations to avoid an N+1.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `evidence-persistence`: adds a pure Core staleness window evaluation; records the
  collecting collector's `collector_id` and `frequency` on each run via a forward-only
  migration; and changes the read store's derived status to a per-collector projection
  that gains a `Stale` state and a batch read over a set of organisations.
- `evidence-ingest`: the ingest endpoint records the resolved collector's identity
  (`collector_id`) and cadence (`frequency`) on the appended run, so staleness is
  derivable from the run itself.
- `statement-of-applicability`: the view page surfaces each collector check's derived
  evidence status, rendering `Stale` ("collection stopped") distinct from `Unknown`
  (never collected). This reverses the "no live evidence results" non-goal of the
  earlier drill-down change for collector checks only.

## Impact

- Affected code (all MIT, in `Freeboard.Core`, `Freeboard.Persistence`, and the web
  app; no EE code):
  - `src/Freeboard.Core/GitOps/EvidenceCollectorFrequency.cs` new pure cadence policy
    (window, grace, `IsStale`, shared token set); `ConfigValidator.cs` reuses the
    shared token set instead of its private copy.
  - `src/Freeboard.Persistence/Migrations/015_evidence_collector_identity.sql` new
    migration adding nullable `collector_id` and `frequency` columns as a single additive
    `ALTER TABLE` (no new index, no backfill, no trigger change).
  - `src/Freeboard.Persistence/IEvidenceWriteStore.cs` (`NewEvidenceRun` gains
    `CollectorId` and `Frequency`), `MySqlEvidenceWriteStore.cs` (writes both).
  - `src/Freeboard.Persistence/EvidenceReadModels.cs` (`EvidenceRunRow` gains
    `CollectorId`, `Frequency`; replaces `AssessmentResultRow` with
    `CollectorEvidenceStatusRow` and adds the `Stale` status),
    `IEvidenceStore.cs` / `MySqlEvidenceStore.cs` (per-collector batch derivation with a
    `TimeProvider`), `PersistenceServiceCollectionExtensions.cs`
    (`AddEvidenceStore` registers `TimeProvider.System`).
  - `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs` passes the collector id and
    cadence into the appended run.
  - `src/Freeboard/Program.cs` registers `AddEvidenceStore`;
    `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml.cs` and `.cshtml`
    batch-read and render the per-collector status.
- Affected tests: new `Freeboard.Core` unit tests for the cadence windows, grace
  boundaries, and unknown tokens; new MySQL integration cases in
  `tests/Freeboard.Persistence.Tests/EvidenceIntegrationTests.cs` (stale, stale
  downgrades passing, stale-hard-failure stays HardFailure, legacy `collector_ref`
  fallback attributes a null-`collector_id` run's verdict, a legacy null-cadence run is
  never stale, one stale collector not hidden by a fresh sibling); a new
  `FakeEvidenceStore` read double in
  `tests/Freeboard.Web.Tests/FakeEvidenceStores.cs` registered on `AuthWebFactory`, plus
  web page tests in `StatementOfApplicabilityPageTests.cs` proving `Stale` and `Unknown`
  render distinctly. MySQL cases skip without `FREEBOARD_TEST_DB`.
- No new runtime dependency. No wire-contract or JSON-schema change.

## Non-goals

- No configurable or per-collector grace or window override; the window and grace are
  fixed constants in `Freeboard.Core`.
- No change to the run verdict (`Pass`/`Fail`), the append-only guarantees, the
  idempotency key, or the `freeboard.evidence.v1` payload contract.
- No retroactive backfill and no rewrite of run content. The migration is purely
  additive; pre-migration runs (and any run with no recorded cadence) keep a null cadence
  and are never flagged stale. Staleness covers only evidence collected after migration
  015 ships, self-healing as collectors report post-migration. A collector that has
  ALREADY stopped keeps its last verdict, because it never reports again to record a
  cadence; this residual false-green for pre-existing stopped collectors is a known,
  bounded limitation, accepted to preserve the `evidence_runs` append-only integrity
  guarantee (no trigger drop, no UPDATE of appended rows).
- No down-migration; migrations are forward-only.
- No requirement-level roll-up of collector statuses and no `Unknown`/`Stale` surfacing
  for attestation checks (attestations carry no cadence). Staleness is a collector
  concept per the issue.
- Not an EE feature; nothing moves into `src/Freeboard.Enterprise`.
