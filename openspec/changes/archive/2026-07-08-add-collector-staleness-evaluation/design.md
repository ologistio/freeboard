## Context

Evidence is append-only. `IEvidenceStore` (in `Freeboard.Persistence`) returns runs
and, today, derives an `AssessmentResultRow` per `(organisation, requirement)` pair
from that pair's single latest run and its checks. Status is a string drawn from
`{HardFailure, SoftFailure, Passing}`; `NoEvidence` is a separate caller-derived string
the store never emits. `DeriveStatus` in `MySqlEvidenceStore` produces the three
values from the checks. `GetAssessmentResultsAsync` has no production consumer: the
only callers are `tests/Freeboard.Persistence.Tests/EvidenceIntegrationTests.cs`, and
the web app registers only `AddEvidenceWriteStore`, never the read store.

Each `EvidenceCollectorRow` carries a required `Frequency` from a closed token set
defined in `Freeboard.Core.GitOps.ConfigValidator` (`FrequencyTokens`): `continuous`,
`daily`, `weekly`, `monthly`, `quarterly`, `annual`. That cadence is not recorded on
the runs a collector produces.

The ingest endpoint (`EvidenceIngestEndpoints.IngestAsync`) already authenticates the
collector, validates the payload `collector_id` against the credential, resolves the
`EvidenceCollectorRow`, and composes `collector_ref` as `collector_id:run_id` (both
parts are validated to exclude `:`, so the composition is unambiguous). So the
collector identity and its cadence are both already in hand at append time.

The codebase has no clock abstraction; stores call `DateTime.UtcNow` directly (for
example `MySqlEvidenceWriteStore` sets `var now = DateTime.UtcNow`).

The Statement of Applicability drill-down (`StatementOfApplicability.ResolveDrilldown`
in `src/Freeboard/Compliance`) projects Organisation -> Requirement -> Control ->
Check. A collector check is a `SoaCheckNode` (`Kind == SoaCheckKind.Collector`) whose
`Id` equals the collector's registration id and which already carries `Frequency`. The
check catalogue is built once and shared across organisation nodes, so per-organisation
evidence status cannot be baked into the shared node instances.

All affected code is MIT: `Freeboard.Core` (pure evaluation), `Freeboard.Persistence`
(schema, write, read), and the web app. Nothing touches `Freeboard.Enterprise`, and the
reference graph (Persistence -> Core, Core references nothing, web combines both) is
preserved.

## Alternatives considered

Each choice below states the option taken and why it beat the alternative. The
decisions themselves are detailed in D1-D6.

- Derivation granularity: per-collector, not per-requirement. Deriving one status per
  `(organisation, requirement)` from that pair's single latest run hides a stopped
  collector whenever a sibling collector produces fresher evidence for the same
  requirement - the fresh run wins the "latest" tie and the stalled collector never
  surfaces. Grouping by `(organisation, requirement, collector)` and evaluating each
  collector's own latest run exposes the stalled one. See D1.

- Collector identity: a first-class `collector_id` column, not identity parsed from the
  `collector_ref` idempotency key. `collector_id` is technically derivable from the
  `collector_ref` prefix for every collector run, but a durable, first-class column is
  the correct home for identity; the prefix parse is kept only as the fallback for
  pre-migration rows that predate the column. `frequency` is denormalised alongside it
  (not left to a join against the mutable collector config) so the read store evaluates
  staleness from the evidence tables alone, preserving its "evidence tables only, one
  `RepeatableRead` snapshot" boundary. Both columns are nullable and additive. See D1.

- Cadence-scaled grace, not a fixed absolute grace. A fixed grace is wrong at both ends
  of the cadence range: a 1-day grace doubles a `continuous` collector's tolerance while
  being negligible for an `annual` one. A grace proportional to the window shrinks as a
  fraction of the window as the cadence lengthens. Sub-day windows (rather than
  day-granular) let `continuous` be judged in hours. See D2.

- Status vocabulary: a new `Stale` string literal, not a new C# enum. The existing
  statuses are string literals from `DeriveStatus`; adding a literal avoids an enum
  refactor with no behavioural gain. `Stale` sits below `HardFailure` in precedence
  because a known hard failure is the most actionable signal and is not itself a false
  green. See D3.

- Time source: `TimeProvider` introduced only on the new staleness path, not a repo-wide
  clock refactor. `AddEvidenceStore` registers `TimeProvider.System` and
  `MySqlEvidenceStore` reads `timeProvider.GetUtcNow().UtcDateTime`, while the pure Core
  function still takes `nowUtc` as an explicit parameter so unit tests stay clock-free.
  See D4.

- Migration: a forward-only, purely additive nullable-column migration, not a backfill.
  A backfill `UPDATE` of already-appended rows would require dropping the append-only
  guard trigger during a non-atomic migration, risking the integrity guarantee. The
  additive migration leaves pre-migration rows with a null cadence, so they are never
  stale (they keep their last-known verdict) and the append-only guard is never touched.
  See D5.

- Surfacing: per-collector status shown on the Statement of Applicability page, with
  `Stale` distinct from `Unknown`, read for all visible organisations in one batched call
  to avoid an N+1. The status is advisory display, read on a separate connection and
  snapshot from the config-tree drill-down. See D6.

- Cadence policy placement: in Core next to the frequency vocabulary, reused by config
  validation, evaluating against `collected_at`. This removes the duplicate frequency
  token set rather than adding a second one. See D5.

## Decisions

### D1: Record collector identity and cadence on the run; derive per collector on read

The read store must know each collector's identity (to group) and expected interval (to
judge overdue-ness) without reading the compliance domain
(`evidence_collectors -> controls -> control_requirements -> requirements`), which the
evidence store deliberately does not join. Both are denormalised onto the run at ingest:
`collector_id` from the authenticated `validated.CollectorId`, `frequency` from the
resolved `EvidenceCollectorRow.Frequency`. The read store then groups a pair's
collector-kind runs by `(organisation, requirement, collector)`, pins each group's
latest run by the existing order, and evaluates that latest run.

Correctness for append-only history: a run is judged against the cadence in force when
it arrived, which is the cadence that set the expectation for the next run. Relaxing a
collector's cadence in config does not un-stale already-collected runs until the next
run records the new cadence; this self-heals on the next collection and is accepted (see
Risks).

Legacy identity fallback (kept): every pre-migration run has a null `collector_id` (the
migration is additive and does not backfill), so the effective collector id is the prefix
of `collector_ref` before the first `:` when `kind` is `Collector` and the delimiter is
present. Ingest has always composed `collector_ref = collector_id:run_id`, so this
recovers the identity of historical rows. The fallback earns its place without the
backfill: it groups a legacy collector's runs so that collector's latest verdict
(`Passing`, `SoftFailure`, or `HardFailure`) is still attributed to it, instead of the
collector reading as `Unknown` ("never collected") despite having real evidence. A run
with no recoverable collector id (an attestation, or a collector run with no delimiter) is
not attributed to a collector and never produces a collector status.

Legacy cadence and staleness: a pre-migration run records no cadence, and the additive
migration leaves its `frequency` null. Its cadence yields no window, so a legacy run is
NEVER `Stale` - it keeps showing its last-known verdict. This means a collector that had
already stopped before migration 015 keeps its last (possibly `Passing`) verdict and is
not flagged stale, because it never reports again to record a cadence. This residual
false-green for pre-existing stopped collectors is a known, bounded limitation, accepted
to preserve the append-only integrity guarantee (no trigger drop, no UPDATE of appended
rows). Staleness is forward-only: it applies to runs collected after 015 ships and
self-heals as each live collector's next collection records its cadence.

### D2: Cadence-scaled grace, sub-day windows

Chosen: sub-day windows with a cadence-scaled grace, over day-granular windows with a
fixed 1-day grace. Cadence-scaled grace wins because a fixed absolute
grace is wrong at both ends of the range - a 1-day grace on a `continuous` collector
doubles its tolerance (an always-on collector silent for a day has effectively stopped),
while a 1-day grace on an `annual` collector is negligible. The window is the maximum
expected interval between collections; the grace is a proportional allowance for one
late cycle's scheduling jitter and clock skew, shrinking as a fraction of the window as
the cadence lengthens.

| frequency  | window | grace | stale when age exceeds |
| ---------- | ------ | ----- | ---------------------- |
| continuous | 1h     | 15m   | 1h 15m                 |
| daily      | 1d     | 6h    | 30h                    |
| weekly     | 7d     | 1d    | 8d                     |
| monthly    | 31d    | 3d    | 34d                    |
| quarterly  | 92d    | 7d    | 99d                    |
| annual     | 366d   | 30d   | 396d                   |

Windows are set at the upper bound of each period (31/92/366 days) so a boundary
collection never false-flags despite calendar month/quarter length. A run is stale when
`nowUtc - collectedAtUtc > window + grace`. A run whose recorded cadence is null, blank,
or not a known token yields no window and is never stale. These constants live in
`EvidenceCollectorFrequency`; the spec scenarios in `evidence-persistence` use values
that agree with this table exactly (a `daily` run aged 2 days is stale because 2d > 30h;
a `weekly` run aged 5 days is fresh because 5d < 8d; a `continuous` run aged 90 minutes
is stale because 90m > 75m).

### D3: Status vocabulary and precedence

`Status` is a string, not a C# enum (the existing values are string literals from
`DeriveStatus`). This change adds the string `Stale`; it introduces no enum type. Per
collector, over that collector's latest run, precedence is (most severe first):

1. `HardFailure` - the latest run has a failing `Hard` check.
2. `Stale` - not a hard failure, but the latest run is past its window plus grace.
3. `SoftFailure` - fresh, no failing hard check, but a failing soft check.
4. `Passing` - fresh, no failing check.

`Unknown` is orthogonal and caller-derived: the store returns a status only for a
collector that has at least one run, exactly as it never emits `NoEvidence` today. The
SoA page derives `Unknown` for a collector check that is configured (expected) but has
no store row. `Unknown` is this change's name for the "never collected" state the issue
calls out; the store still emits none of these caller-derived values.

Rationale: the issue's concern is a false green, so `Stale` overrides `Passing` and
`SoftFailure`. `HardFailure` outranks `Stale`: a definite last-known hard failure is
more actionable than "we stopped hearing", and neither is a false green.

### D4: Time source

No clock abstraction exists repo-wide; this change does not introduce one broadly. It
registers `TimeProvider.System` only in `AddEvidenceStore` and injects `TimeProvider`
into `MySqlEvidenceStore`, which calls `timeProvider.GetUtcNow().UtcDateTime` and passes
it to `EvidenceCollectorFrequency.IsStale(collectedAtUtc, frequency, nowUtc)`. The pure
Core function takes `nowUtc` as a parameter, so unit tests are deterministic without a
clock. Integration tests exercise staleness by inserting a far-past `collected_at`, so
the live clock reliably exceeds the window.

### D5: Placement and schema

- Pure evaluation and the frequency vocabulary in
  `Freeboard.Core.GitOps.EvidenceCollectorFrequency` (MIT, no deps): the token set, the
  cadence-to-window/grace map, and `IsStale`. `ConfigValidator` reuses the shared token
  set instead of its private `FrequencyTokens` copy, removing the duplicate vocabulary.
- `evidence_runs.collector_id` is a nullable `VARCHAR(190)` `utf8mb4_bin` (matching the
  id column convention) and `evidence_runs.frequency` a nullable `VARCHAR(16)`, added by
  a new forward-only migration `015_evidence_collector_identity.sql` (014 is the latest).
  There is NO foreign key to `evidence_collectors`: evidence must survive GitOps churn or
  deletion of a collector, consistent with the existing scalar
  `organisation_id`/`requirement_id` columns.
- No new index. The proposed
  `(organisation_id, requirement_id, collector_id, collected_at)` index shares its
  leftmost `(organisation_id, requirement_id)` prefix with the existing
  `ix_evidence_runs_org_requirement_collected` `(organisation_id, requirement_id,
  collected_at)` from migration 011. The batch read filters collector-kind runs by
  `organisation_id` (and requirement), which that existing index already serves via its
  leftmost prefix; per-collector grouping and latest-run pinning happen in the read store
  after the fetch, so inserting `collector_id` into a dedicated fourth index buys no read
  path it would use while adding a write on every append to this append-hot table.
  Decision: drop the new index and rely on `ix_evidence_runs_org_requirement_collected`.
- The migration is purely additive: a single `ALTER TABLE evidence_runs` adding the two
  nullable columns (`collector_id VARCHAR(190)` `utf8mb4_bin`, `frequency VARCHAR(16)`),
  matching the repo idiom (migrations 005 and 012 use bare `ADD COLUMN`). There is no
  backfill `UPDATE`, no `DROP`/`CREATE` of `trg_evidence_runs_no_update`, and no new
  index. The append-only BEFORE UPDATE guard is never touched, so the append-only
  integrity guarantee holds through the migration. Pre-migration rows read back null for
  both columns; the read store's identity fallback still attributes them, and their null
  cadence keeps them out of staleness (see D1).
- Replay-safety. The repo's actual `ALTER` convention is plain, non-guarded DDL:
  migrations 005 and 012 use bare `ADD COLUMN`, and no migration uses an
  `information_schema` guard (MySQL 8.4 has no `ADD COLUMN IF NOT EXISTS`). Introducing
  such a guard just for 015 would be a new idiom with no precedent, so it is rejected as
  unwarranted liability. Per `MySqlMigrationRunner.ApplyOneAsync`, the runner runs the
  migration SQL and only then records the version; the two steps are NOT transactionally
  atomic. Because plain `ADD COLUMN` is not idempotent, a crash after the `ALTER` commits
  but before the `schema_migrations` version row is written makes a re-run fail on the
  duplicate column. Operational recovery (stated in the migration header): drop the
  partially-added `collector_id`/`frequency` columns and re-run `freeboard system
  migrate`, or record the `schema_migrations` row for version `015` by hand.

### D6: Read-store surface and SoA wiring

- Replace `AssessmentResultRow` and `GetAssessmentResultsAsync` (per-requirement,
  test-only, no production consumer) with a per-collector projection:
  `CollectorEvidenceStatusRow(OrganisationId, RequirementId, CollectorId, Status,
  LastCollectedAt)` and
  `GetCollectorEvidenceStatusesAsync(IReadOnlyCollection<string> organisationIds, ...)`.
  A single method returns statuses for all visible organisations, so the SoA page issues
  one read. This consolidates onto one derivation rather than running a per-requirement
  and a per-collector system side by side.
- Register the read store in the web app (`Program.cs` gains `AddEvidenceStore`), which
  is not registered today. `StatementOfApplicabilityModel` injects `IEvidenceStore`,
  batch-reads statuses for the in-scope node ids inside its existing store-failure
  try/catch, and builds a lookup keyed by
  `(organisationId, requirementId, collectorId)`. The `.cshtml` renders a status badge
  on each collector check, looking up the status (defaulting to `Unknown` when absent),
  with `Stale` shown as "collection stopped" distinct from `Unknown`. The evidence read
  is a separate connection and snapshot from the compliance drill-down read: the status
  is advisory display, not part of the config-tree consistency guarantee.
- `ResolveDrilldown` and `SoaCheckNode` are unchanged, so the shared, org-independent
  check catalogue is not duplicated per organisation and the pure projection keeps its
  existing tests.

## Risks / Trade-offs

- A cadence relaxed in config does not un-stale already-collected runs until the next run
  records the new cadence. Self-heals on the next collection; the window in force when a
  run arrived is the correct expectation for that run.
- A brand-new collector that never emitted a run produces no row, so it reads `Unknown`,
  not `Stale`. Correct by definition: `Stale` requires prior evidence.
- No backfill: pre-migration runs (and any run with no recorded cadence) keep a null
  cadence and are never `Stale`. A collector that had already stopped before migration 015
  keeps its last verdict - a residual false-green for pre-existing stopped collectors.
  Accepted, bounded limitation: staleness is forward-only and self-heals as live collectors
  report post-migration, and the alternative (a backfill `UPDATE`) would require dropping
  the append-only guard, which this change deliberately preserves.
- Migration 015 is a single additive `ALTER` but the runner is not transactionally atomic
  with the `schema_migrations` insert, and plain `ADD COLUMN` is not idempotent. A crash
  between the `ALTER` and the version write makes a re-run fail on the duplicate column.
  Mitigated by the D5 operational recovery (drop the added columns and re-run, or record
  the version by hand); the exposure window is a single controlled migration run and the
  append-only guard is never dropped.
- Fixed day-count windows ignore calendar month/quarter length. Windows sit at the upper
  bound plus grace, so a boundary collection never false-flags; the cost is a slightly
  later stale flag for short months, which is safe.
- `Stale` sits below `HardFailure`, so a hard-failing collector that then stops shows
  `HardFailure`. Intentional (D3): not a false green, and the most actionable signal.
- A future-dated `collected_at` would suppress staleness. The ingest requires a UTC
  `collected_at` but does not bound future skew; large forward skew is out of scope here
  and can be addressed by an ingest skew check later.
- The SoA evidence read is a second round trip per page render (batched, one call). It is
  not in the drill-down snapshot; acceptable for an advisory status.

## Migration Plan

1. Ship migration `015_evidence_collector_identity.sql` (a single additive `ALTER TABLE`
   adding the two nullable columns, see D5); operators apply it with `freeboard system
   migrate`. It changes no data and does not touch the append-only trigger.
2. New collector runs record `collector_id` and `frequency` at ingest, so staleness
   applies forward-only. Pre-migration rows keep null on both columns: they are attributed
   via the `collector_ref` fallback (identity, so their last verdict still shows) but are
   never flagged stale (null cadence); non-collector runs stay null on both. A collector
   that had already stopped keeps its last verdict - the accepted, bounded limitation.
3. If the migration fails partway, follow the operational recovery in D5 (drop the added
   columns and re-run, or record the version by hand) - it is not atomically replay-safe
   because plain `ADD COLUMN` is not idempotent.
4. Rollback: the columns are additive and ignored by older code paths; migrations are
   forward-only, so no down-migration is authored.

## Open Questions

- Should the read store instead evaluate staleness against the collector's current
  cadence (live) so a config change takes effect immediately, rather than the cadence
  recorded on the run? Chosen: recorded cadence (D1).
- Are the window constants and cadence-scaled grace acceptable defaults, or should
  `continuous` and `annual` use different bounds before a broader status view ships?
