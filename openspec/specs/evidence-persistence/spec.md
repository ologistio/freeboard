# evidence-persistence Specification

## Purpose
TBD - created by archiving change add-evidence-persistence. Update Purpose after archive.
## Requirements
### Requirement: MySQL schema and migration for evidence

The system SHALL persist runtime compliance evidence in MySQL via a forward-only
migration applied by `freeboard system migrate`. The migration SHALL create an
`evidence_runs` table (one row per collector or attestation run), an
`evidence_checks` table (one row per named check within a run), and an
`attestation_responses` table (a 1:1 extension of an attestation run). Every id
and foreign-key column SHALL use `utf8mb4_bin` collation to match the exact-byte
id identity used elsewhere in the schema. The migration SHALL be idempotent on
replay so a partially applied migration re-runs cleanly. This code SHALL live in
the MIT `Freeboard.Persistence` project and SHALL NOT add any reference to
`Freeboard.Enterprise` or any new dependency.

#### Scenario: Migration applies cleanly on a fresh database

- **WHEN** `freeboard system migrate` runs against a database with migrations
  001-010 applied
- **THEN** migration 011 applies successfully and the `evidence_runs`,
  `evidence_checks`, and `attestation_responses` tables exist

#### Scenario: Partial migration re-runs cleanly

- **WHEN** migration 011 is re-applied after a prior partial failure left some of
  its objects created
- **THEN** it completes without error because its table and trigger creation is
  idempotent (`IF NOT EXISTS`, or `DROP TRIGGER IF EXISTS` before each
  `CREATE TRIGGER`)

### Requirement: Evidence run captures a single collector observation

An `evidence_runs` row SHALL record one run and SHALL persist: a
runtime-generated ULID `id`; a `kind` discriminator (`Collector` or
`AttestationResponse`, stored as the PascalCase enum name to match the schema's
existing enum-string convention); the logical `organisation_id` and
`requirement_id` the run attaches to; the non-null `collector_ref` idempotency key
that identifies the specific observation; a non-null `vendor` source string; a nullable
`collector_id` recording the id of the collector that produced the run (null for a run
with no collector, including every attestation run); a nullable `frequency` recording
that collector's cadence token (null for a run with no cadence, including every
attestation run); an overall `result` (`Pass` or `Fail`); the `collected_at` timestamp
of the observation; an optional opaque `raw_payload` stored as JSON; and a `created_at`
insert timestamp. The `organisation_id`, `requirement_id`, and (for attestations)
`user_id` SHALL reference `organisations(id)`, `requirements(id)`, and `users(id)`
respectively. The default strategy is scalar id columns WITHOUT a strict foreign
key, following the `authz_audit_events` precedent, so a recorded run survives
deletion or gitops churn of the referenced organisation, requirement, or user;
`collector_id` follows the same no-foreign-key rule so a run survives deletion of its
collector. Both `vendor` and `collector_ref` SHALL be `NOT NULL` so that re-delivery of
the same observation is rejected by the `UNIQUE (vendor, collector_ref)` key (MySQL
treats `NULL` as distinct, so a nullable column would not dedup).

#### Scenario: A collector run is appended with its identifying fields

- **WHEN** a collector run is appended for an organisation and requirement with a
  collector ref, vendor, collector id, cadence, result, timestamp, and raw payload
- **THEN** one `evidence_runs` row exists carrying all those fields with `kind` =
  `Collector`, its `collector_id` recording the collector, and its `frequency` recording
  the collector's cadence

#### Scenario: Re-delivering the same observation is idempotent

- **WHEN** a run is appended with a `vendor` and `collector_ref` that already
  exist on a recorded run
- **THEN** the duplicate is rejected by the `UNIQUE (vendor, collector_ref)` key
  and surfaced as a failing write result, leaving the original run unchanged

#### Scenario: Evidence survives removal of its requirement

- **WHEN** the requirement an evidence run references is later deleted from the
  compliance definition
- **THEN** the delete is not blocked by the evidence run and the run remains,
  because `requirement_id` is a scalar column with no strict foreign key

#### Scenario: An attestation run records no collector or cadence

- **WHEN** an attestation run is appended (it has no collector and its template carries
  no cadence)
- **THEN** its `evidence_runs` row records a null `collector_id` and a null `frequency`

### Requirement: Nested named checks carry severity and result

Each named check within a run SHALL be stored as an `evidence_checks` row with a
ULID `id`, its parent `evidence_id`, a `name`, a `severity` of `Hard` or `Soft`,
a `result` of `Pass` or `Fail`, an `ordinal`, and an optional `detail`. A check
SHALL reference its parent run by foreign key. A run SHALL NOT contain two checks
with the same `name`.

#### Scenario: Checks are stored per run with severity

- **WHEN** a run with several named checks is appended
- **THEN** each check is one `evidence_checks` row linked to the run, carrying its
  `Hard` or `Soft` severity and its result

#### Scenario: Duplicate check names within a run are rejected

- **WHEN** a run is appended with two checks sharing the same `name`
- **THEN** the append fails on the unique `(evidence_id, name)` constraint and no
  partial run is left behind

### Requirement: Attestation response is a specialised evidence run

An attestation SHALL be persisted as an `evidence_runs` row with `kind` =
`AttestationResponse` plus a 1:1 `attestation_responses` row keyed on
`evidence_id` that records the respondent `user_id`, whether the quiz was passed,
and an optional score. The per-question answers SHALL be stored as the run's
`evidence_checks` rows, so the assessment derivation consumes attestation and
collector evidence through the same check model. An attestation SHALL be subject
to the same append-only rules as any other evidence run.

#### Scenario: An attestation is appended as evidence plus an extension row

- **WHEN** an attestation is appended for a user with per-question answers and a
  quiz outcome
- **THEN** one `evidence_runs` row with `kind` = `AttestationResponse`, one
  `attestation_responses` row keyed on its `evidence_id`, and one
  `evidence_checks` row per answered question exist

### Requirement: Evidence is append-only and immutable

The system SHALL prevent in-place mutation of a recorded evidence run. The write
store SHALL expose only append operations and SHALL provide no method that
updates or deletes a recorded run or its checks. The database SHALL enforce this
independently: `BEFORE UPDATE` and `BEFORE DELETE` triggers on `evidence_runs`,
`evidence_checks`, and `attestation_responses` SHALL reject any such statement.

#### Scenario: Updating a recorded run is rejected by the database

- **WHEN** any client issues an UPDATE against a row in `evidence_runs`,
  `evidence_checks`, or `attestation_responses`
- **THEN** the database rejects the statement via a `SIGNAL` error and the row is
  unchanged

#### Scenario: Deleting a recorded run is rejected by the database

- **WHEN** any client issues a DELETE against a row in `evidence_runs`,
  `evidence_checks`, or `attestation_responses`
- **THEN** the database rejects the statement via a `SIGNAL` error and the row
  remains

#### Scenario: The write store has no mutation method

- **WHEN** a consumer holds the evidence write store
- **THEN** the only available operations append new runs; there is no compile-time
  method to update or delete an existing run

### Requirement: Read store surfaces evidence and derived assessment status

The system SHALL provide a read store that returns evidence runs (with their nested
checks resolved) for an organisation and requirement, and that computes a per-collector
evidence status for each `(organisation, requirement, collector)` that HAS at least one
collector-kind run, from that collector's latest run and its checks. The status SHALL be
derived on read (no stored assessment table) and SHALL describe the evidence, not
compliance: it means "this collector's latest run has no failing hard check and is not
overdue", not "the requirement is satisfied". The store SHALL expose a batch read that
returns the per-collector statuses for a supplied set of organisations in one call, so a
caller rendering many organisations does not issue a read per organisation.

A run's collector identity SHALL be its `collector_id` column when present; for a
pre-migration run whose `collector_id` is null (the additive migration does not backfill
it), the identity SHALL fall back to the prefix of `collector_ref` before the first `:`
when `kind` is `Collector` and the delimiter is present (ingest composes `collector_ref`
as `collector_id:run_id`). This fallback SHALL ensure a legacy collector's runs are still
grouped to it, so its latest verdict is attributed correctly rather than the collector
appearing to have no evidence. A run with no recoverable collector identity SHALL NOT
produce a collector status.

A pre-migration run records no cadence, so its `frequency` is null and it SHALL never be
`Stale` - its latest verdict (`HardFailure`, `SoftFailure`, or `Passing`) still shows.
Any run whose recorded cadence is null (every pre-migration run, every attestation run) or
yields no window SHALL never be `Stale`. Staleness is therefore forward-only: it applies
to runs collected after the migration ships, and a collector that had already stopped
keeps its last verdict, an accepted, bounded limitation of not backfilling.

Both the run-overall `result` and each per-check `result` SHALL draw from the closed set
`{Pass, Fail}`; "failing" means `result == Fail`. Over a collector's latest run's checks
and its recorded cadence, evaluated against the current time obtained from an injected
`TimeProvider`, the precedence (most severe first) SHALL be: any `Hard` check with
`result == Fail` yields `HardFailure`; otherwise a latest run that is stale (its
`collected_at` is older than the window plus grace derived from its recorded `frequency`,
per the collector staleness window evaluation) yields `Stale`; otherwise any `Soft` check
with `result == Fail` yields `SoftFailure`; otherwise `Passing`. A latest run whose
recorded cadence yields no window is never `Stale`. Grouping by collector SHALL ensure a
stopped collector is not masked by a different collector that produced fresher evidence
for the same requirement.

The staleness evaluation SHALL read only the evidence tables: the latest run's
`collected_at`, `collector_id`, and `frequency` are columns on `evidence_runs`, so the
derivation does NOT read scopes or collector configuration and preserves its single
`RepeatableRead` snapshot.

The latest run for a collector SHALL be pinned deterministically by
`ORDER BY collected_at DESC, received_at DESC, created_at DESC, id DESC` (the ULID `id`
is monotonic and gives a total-order tie-break).

The store SHALL return a status only for a collector that has a run and SHALL NOT emit an
`Unknown` status. Deriving `Unknown` for a configured (expected) collector that has no
store row is the caller's responsibility; `Unknown` is a distinct status from `Stale`
(`Stale` means prior collector evidence exists but has gone overdue, `Unknown` means the
collector never produced evidence).

#### Scenario: A collector's latest run with a failing hard check assesses as HardFailure

- **WHEN** the latest run for an `(organisation, requirement, collector)` has a failing
  `Hard` check
- **THEN** the computed status for that collector is `HardFailure`

#### Scenario: An overdue passing collector assesses as Stale, not Passing

- **WHEN** a collector's latest run has no failing check but was collected longer ago
  than the window plus grace derived from its recorded `daily` cadence
- **THEN** the computed status for that collector is `Stale`, not `Passing`, so a stopped
  collector is not shown as a false green

#### Scenario: A hard failure outranks staleness

- **WHEN** a collector's latest run has a failing `Hard` check and is also older than its
  window plus grace
- **THEN** the computed status is `HardFailure`, because a known hard failure is the most
  severe status and outranks `Stale`

#### Scenario: A stale collector is not masked by a fresh sibling collector

- **WHEN** two collectors verify the same requirement, one collector's latest run is
  overdue for its cadence, and the other collector's latest run is fresh and passing
- **THEN** the overdue collector's status is `Stale` and the fresh collector's status is
  `Passing`, because derivation is per collector rather than per the requirement's single
  latest run

#### Scenario: A collector's latest run with only a failing soft check assesses as SoftFailure

- **WHEN** a collector's latest run has no failing `Hard` check, is not overdue, but has
  a failing `Soft` check
- **THEN** the computed status for that collector is `SoftFailure`

#### Scenario: A collector with no evidence run has no store status

- **WHEN** a collector configured to verify a requirement has no evidence run for an
  organisation
- **THEN** the read store returns no status for that collector; the caller derives
  `Unknown` for it, distinct from `Stale`

#### Scenario: A pre-migration run is attributed by its collector_ref prefix but is never stale

- **WHEN** a collector-kind run has a null `collector_id`, a null `frequency`, and a
  `collector_ref` of the form `collector_id:run_id`
- **THEN** the run is attributed to the collector named by the `collector_ref` prefix and
  contributes its verdict to that collector's status, and the collector is never `Stale`
  because the run carries no cadence

#### Scenario: Only the latest run per collector counts

- **WHEN** an earlier run for a collector had a `Hard` check with `result` = `Fail` but a
  later run for the same collector (higher `collected_at`) has all checks `Pass`
- **THEN** the computed status for that collector is derived from the latest run pinned by
  `collected_at`, `received_at`, `created_at`, `id` descending - `Passing` when that
  latest run is fresh or `Stale` when it is overdue - and the earlier run's rows are
  unchanged

### Requirement: Evidence persistence integration tests are gated on FREEBOARD_TEST_DB

The MySQL integration tests for evidence persistence SHALL run against the real
database named by `FREEBOARD_TEST_DB` and SHALL skip cleanly when that variable is
unset, using the existing `SkippableFact` / `RequiresEnvVarFact` and
`MySqlTestDatabase` infrastructure.

#### Scenario: Tests skip when the database is not configured

- **WHEN** `dotnet test` runs with `FREEBOARD_TEST_DB` unset
- **THEN** the evidence integration tests are skipped, not failed

#### Scenario: Tests run when the database is configured

- **WHEN** `FREEBOARD_TEST_DB` points at a reachable MySQL and `dotnet test` runs
- **THEN** the evidence integration tests provision a throwaway database, apply
  migrations, and exercise the append, idempotency, immutability, and
  assessment-derivation behaviour

### Requirement: Collector staleness window evaluation

The system SHALL provide a pure evaluation in `Freeboard.Core` that owns the collector
`frequency` vocabulary, maps each cadence token to a staleness window plus a
cadence-scaled grace, and decides whether a run is overdue, so the rule is
unit-testable without a database or a clock. The `ConfigValidator` frequency-token check
SHALL reuse this shared vocabulary rather than a duplicate token set. The window and
grace per cadence token SHALL be: `continuous` window 1 hour grace 15 minutes; `daily`
window 1 day grace 6 hours; `weekly` window 7 days grace 1 day; `monthly` window 31 days
grace 3 days; `quarterly` window 92 days grace 7 days; `annual` window 366 days grace 30
days. The day-count windows are calendar-agnostic and set at the upper bound of each
period so a boundary collection is not falsely flagged. A run whose recorded cadence is
null, blank, or not one of the known tokens SHALL yield no window and SHALL never be
stale. Given a run's `collected_at`, its recorded cadence, and the current time, the run
SHALL be stale when `now - collected_at > window + grace`. The evaluation SHALL take the
current time as a parameter (no clock abstraction is introduced into Core); the read
store supplies it from an injected `TimeProvider`.

#### Scenario: A known cadence yields its window plus grace

- **WHEN** the evaluation is asked whether a `daily` run collected 2 days before the
  supplied now is stale
- **THEN** it returns stale, because 2 days exceeds the 1-day window plus 6-hour grace
  (30 hours)

#### Scenario: A run inside its window plus grace is fresh

- **WHEN** the evaluation is asked whether a `weekly` run collected 5 days before the
  supplied now is stale
- **THEN** it returns not stale, because 5 days is within the 7-day window plus 1-day
  grace

#### Scenario: A continuous cadence uses a sub-day window

- **WHEN** the evaluation is asked whether a `continuous` run collected 90 minutes before
  the supplied now is stale
- **THEN** it returns stale, because 90 minutes exceeds the 1-hour window plus 15-minute
  grace

#### Scenario: Unknown or absent cadence is never stale

- **WHEN** the evaluation is asked about a run whose recorded cadence is null, blank, or
  an unrecognised token, regardless of age
- **THEN** it returns not stale, because no window can be derived

### Requirement: Migration adds collector identity and cadence to evidence runs

A forward-only migration applied by `freeboard system migrate` SHALL add to the existing
`evidence_runs` table a nullable `collector_id VARCHAR(190)` column with `utf8mb4_bin`
collation (recording the id of the collector that produced the run) and a nullable
`frequency VARCHAR(16)` column (recording that collector's cadence token). It SHALL NOT
add a new index; the existing `(organisation_id, requirement_id, collected_at)` index
already serves the batch read via its leftmost prefix and per-collector grouping is done
in the read store, so a further index would only add write cost to the append-hot table.
There SHALL be NO foreign key from `collector_id` to `evidence_collectors`, so a recorded
run survives deletion or gitops churn of the collector, consistent with the scalar
`organisation_id`/`requirement_id` columns.

The migration SHALL be purely additive: a single `ALTER TABLE` adding the two nullable
columns, matching the repo idiom (existing migrations use bare `ADD COLUMN`). It SHALL NOT
backfill any row, SHALL NOT run any `UPDATE` against `evidence_runs`, and SHALL NOT drop
or re-create the append-only `trg_evidence_runs_no_update` BEFORE UPDATE trigger, so the
`evidence_runs` append-only integrity guarantee is preserved through the migration.
Pre-migration runs (and any run appended without a cadence) SHALL therefore read back null
for both new columns; they are never retroactively flagged stale. This is a deliberate,
accepted forward-only limitation: staleness covers only evidence collected after this
migration ships, and a collector that had already stopped keeps its last verdict because
it never reports again to record a cadence.

Because the migration runner runs the migration SQL and only then records the version (the
two steps are not transactionally atomic) and plain `ADD COLUMN` is not idempotent, the
migration is NOT atomically replay-safe: a crash after the `ALTER` commits but before the
version is recorded makes a re-run fail on the duplicate column. The migration header SHALL
document the operational recovery: drop the partially-added columns and re-run, or record
the migration version by hand. This code SHALL live in the MIT `Freeboard.Persistence`
project and SHALL NOT add any reference to `Freeboard.Enterprise` or any new dependency.

#### Scenario: Migration adds the nullable identity and cadence columns

- **WHEN** the migration runs against a database with the evidence migration applied
- **THEN** the `evidence_runs` table gains nullable `collector_id` and `frequency`
  columns

#### Scenario: A pre-migration run keeps null identity and cadence

- **WHEN** the migration runs and a pre-migration collector run already exists
- **THEN** the run's new `collector_id` and `frequency` columns are both null (no
  backfill), its `result`, `collected_at`, `collector_ref`, and checks are unchanged, and
  it is never evaluated as stale

#### Scenario: The append-only guard is untouched by the migration

- **WHEN** the migration runs
- **THEN** the `trg_evidence_runs_no_update` BEFORE UPDATE trigger is never dropped, so a
  stray UPDATE against `evidence_runs` remains rejected throughout and after the migration

