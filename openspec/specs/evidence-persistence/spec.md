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
`requirement_id` the run attaches to; a nullable `asset_id` naming the machine asset the
run describes (null when the run describes the organisation as a whole, which is every
attestation run and every organisation-level collector run); a nullable `collector_ref`
identifying the specific observation; a nullable `vendor` source string; a nullable
`collector_id` recording the id of the collector that produced the run (null for a run
with no collector, including every attestation run); a nullable `cycle_id` recording the
collection cycle the run belongs to (null for a run that no scheduler dispatch produced);
a nullable `frequency` recording that collector's cadence token (null for a run with no
cadence, including every attestation run); an overall `result` from the closed set
`{Pass, Fail, Error}`; an `error_detail` carrying why collection failed, which SHALL be
non-blank when `result` is `Error` and SHALL be null otherwise; the `collected_at`
timestamp of the observation; an optional
opaque `raw_payload` stored as JSON; and a `created_at` insert timestamp. The
`organisation_id`, `requirement_id`, `asset_id`, and (for attestations) `user_id` SHALL be
scalar id references. The default strategy is scalar id columns WITHOUT a strict foreign
key, following the `authz_audit_events` precedent, so a recorded run survives
deletion or gitops churn of the referenced organisation, requirement, machine, or user;
`collector_id` follows the same no-foreign-key rule so a run survives deletion of its
collector.

The machine reference is a DIMENSION under the run's organisation, never a replacement for
it. A machine-scoped run SHALL still record the organisation the machine belongs to in
`organisation_id`, so every roll-up and every authorization narrowing keyed on an
organisation keeps working.

An `Error` result SHALL mean that the collection attempt itself failed, so the state of the
requirement was not observed. It is distinct from `Fail`, which asserts an observed policy
failure. An `Error` run SHALL record its `error_detail`, because an error with no stated
reason records that something failed but not what. An `Error` run MAY carry no checks at
all, and it MAY carry the checks a partial collection did observe before it failed. A check
an errored run carries SHALL keep its full weight in the derived status, because that check
was observed.

A run SHALL carry EXACTLY ONE of two identities, and the database SHALL enforce this with
check constraints: either a non-null `vendor` with a non-null `collector_ref` and a null
`cycle_id`, or a NON-BLANK `collector_id` with a NON-BLANK `cycle_id` and a null `vendor` and
`collector_ref`. A cycle-keyed run therefore names its collector. An empty `collector_id` is
absence, not an identity: the derived status reads a collector only from a non-empty value, so
a run carrying an empty one would be recorded and then assessed for no collector. `vendor`
and `collector_ref` SHALL be both null or both non-null, so a half-identified run cannot
escape the key that dedups it. Each identity backs one idempotency
key. No run can be appended that neither key can dedup, and no run can be appended that both
keys would claim.

A run whose `kind` is not `Collector` records no collector, no cadence, no machine, and no
cycle, so its `collector_id`, `frequency`, `asset_id`, and `cycle_id` are all null whatever
the caller supplied. The `error_detail` is NOT dropped this way: it belongs to the run's
`result`, which every kind carries, so its rule is the same for every kind. Such a run
therefore SHALL carry the `vendor` and `collector_ref`
identity. An append SHALL be validated against the values it will store, so a member the
run's kind drops cannot satisfy a rule and then be stored as null.

Absence SHALL have ONE representation. A `vendor`, a `collector_ref`, a `collector_id`, an
`asset_id`, a `cycle_id`, or an `error_detail` that is empty or holds only whitespace SHALL be
normalized to null before the append is validated and before it is stored. Without the normalization the two layers disagree about
the same row: the application reads a whitespace-only vendor as absent, while the database
reads it as present and admits a run that carries half an identity in substance. Storing the
normalized value makes one rule answer for both.

Two idempotency keys SHALL exist side by side, and they SHALL NOT contend, because the
identity rule admits exactly one of them per run and MySQL treats each `NULL` as distinct:

- `UNIQUE (vendor, collector_ref)` dedups a re-delivered producer-supplied observation.
  `collector_ref` is the producer's stable id for THAT observation.
- `UNIQUE (cycle_id, organisation_id, requirement_id, asset_key)` dedups a re-delivered or
  retried collection cycle, where `asset_key` is the generated `COALESCE(asset_id, _utf8mb4'')`.
  Re-running one collection cycle SHALL therefore append each of its runs exactly once,
  whatever the cycle produced for each machine.

A run appended under the cycle key SHALL be final for that cycle. Evidence is append-only
and a retry reuses the same cycle token, so a later, different answer for the same
`(cycle_id, organisation_id, requirement_id, asset_key)` collides with the recorded run and
is discarded. A producer SHALL therefore append a run only for an outcome it will not retry
under that cycle id. A transient failure the producer intends to retry SHALL NOT be appended
as an `Error` run, because the succeeding retry could not replace it and the collector would
report `Errored` on an outcome that was superseded. An `Error` run records a failure that
ends the cycle's attempt for that machine, such as an unresolvable credential or a provider
rejection a retry would repeat.

#### Scenario: A collector run is appended with its identifying fields

- **WHEN** a collector run is appended for an organisation and requirement with a
  collector ref, vendor, collector id, cadence, result, timestamp, and raw payload
- **THEN** one `evidence_runs` row exists carrying all those fields with `kind` =
  `Collector`, its `collector_id` recording the collector, and its `frequency` recording
  the collector's cadence

#### Scenario: A machine-scoped run records both the machine and its organisation

- **WHEN** a run is appended naming a machine asset in `asset_id`
- **THEN** the row records that machine in `asset_id` and still records the machine's
  organisation in `organisation_id`

#### Scenario: Re-delivering the same observation is idempotent

- **WHEN** a run is appended with a `vendor` and `collector_ref` that already
  exist on a recorded run
- **THEN** the duplicate is rejected by the `UNIQUE (vendor, collector_ref)` key
  and surfaced as a failing write result, leaving the original run unchanged

#### Scenario: Re-running a collection cycle appends each run once

- **WHEN** a collection cycle that already appended one run per machine for an
  organisation and requirement is re-run under the same `cycle_id`
- **THEN** each re-delivered run collides on the
  `(cycle_id, organisation_id, requirement_id, asset_key)` key and is surfaced as a failing
  write result, so the cycle's runs are not duplicated and the recorded runs are unchanged

#### Scenario: One cycle records one run per machine

- **WHEN** one collection cycle appends runs for several machines under one organisation
  and requirement
- **THEN** each run is stored, because the machines differ in `asset_key` and the key
  therefore does not collide

#### Scenario: An organisation-level run in a cycle still dedups

- **WHEN** a run with a null `asset_id` is appended twice under the same `cycle_id`,
  organisation, and requirement
- **THEN** the second append collides, because the generated `asset_key` is the empty
  string on both rows rather than a distinct `NULL`

#### Scenario: A half-identified run is rejected

- **WHEN** a run is appended with a `vendor` but no `collector_ref`, or with a
  `collector_ref` but no `vendor`
- **THEN** the append fails and nothing is recorded, because the legacy identity is
  both-or-neither and a half-identified run would escape the `(vendor, collector_ref)` key

#### Scenario: A blank identity value is treated as absent

- **WHEN** a run is appended whose `vendor` or `collector_ref` holds only whitespace
- **THEN** the blank value is normalized to null before the append is validated, so the run is
  judged by the identity rules exactly as a run that supplied nothing at all, and a run that
  is recorded carries null rather than whitespace in that column

#### Scenario: A blank cycle identity is rejected

- **WHEN** a run is appended with a `cycle_id` and a blank `collector_id`, or with a
  `collector_id` and a blank `cycle_id`
- **THEN** the append fails and nothing is recorded, because a cycle-keyed run names its
  collector and a blank value names none

#### Scenario: A run that neither key can dedup is rejected

- **WHEN** a run is appended with no `vendor` and no `collector_ref` and with no `cycle_id`
- **THEN** the append fails and nothing is recorded

#### Scenario: A run carrying both identities is rejected

- **WHEN** a run is appended with a `vendor` and a `collector_ref` AND a `cycle_id`
- **THEN** the append fails and nothing is recorded, because a run that both keys would
  dedup has two answers to whether it is a duplicate

#### Scenario: A recorded cycle run cannot be corrected within its cycle

- **WHEN** a run recorded under a `cycle_id` for a machine is re-appended under the same
  `cycle_id`, organisation, requirement, and machine with a different result
- **THEN** the append fails as a duplicate and the recorded run keeps its original result,
  so a producer must not append an outcome it intends to retry under that cycle id

#### Scenario: An errored run records why collection failed

- **WHEN** a collection attempt fails and a run is appended with `result` = `Error`, an
  error detail, and no checks
- **THEN** the row records `Error` and its detail, and it is not recorded as `Pass` or
  `Fail`

#### Scenario: An errored run with no detail is rejected

- **WHEN** a run is appended with `result` = `Error` and a null or blank `error_detail`
- **THEN** the append fails and nothing is recorded, because an errored run states why it
  failed

#### Scenario: A detail on a run that did not error is rejected

- **WHEN** a run is appended with `result` = `Pass` or `Fail` and a non-null `error_detail`
- **THEN** the append fails and nothing is recorded, because the detail belongs to an
  errored run only

#### Scenario: Evidence survives removal of its requirement

- **WHEN** the requirement an evidence run references is later deleted from the
  compliance definition
- **THEN** the delete is not blocked by the evidence run and the run remains,
  because `requirement_id` is a scalar column with no strict foreign key

#### Scenario: An attestation run records no collector, cadence, machine, or cycle

- **WHEN** an attestation run is appended (it has no collector and its template carries
  no cadence)
- **THEN** its `evidence_runs` row records a null `collector_id`, a null `frequency`, a
  null `asset_id`, and a null `cycle_id`, its result is the verdict the attestation flow
  derived from the response, and it is identified by its `vendor` and `collector_ref`

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
collector-kind run. The status SHALL be derived on read (no stored assessment table) and
SHALL describe the evidence, not compliance: it means "this collector's latest collection
has no failing hard check, did not error, and is not overdue", not "the requirement is
satisfied". The store SHALL expose a batch read that returns the per-collector statuses for
a supplied set of organisations in one call, so a caller rendering many organisations does
not issue a read per organisation. The returned status SHALL stay keyed on
`(organisation, requirement, collector)` and SHALL NOT gain a machine dimension, so one
collector reports one status however many machines it collected from.

A run's collector identity SHALL be its `collector_id` column when that column is non-null
AND non-empty. An empty `collector_id` names no collector, so it SHALL be read as absent
rather than as an identity. For a run whose `collector_id` is absent by that rule (every
pre-migration run, because the additive migration does not backfill it), the identity SHALL
fall back to the prefix of `collector_ref` before the first `:`, when `kind` is `Collector`
and that prefix is non-empty (ingest composes `collector_ref` as `collector_id:run_id`). A
reference with no `:`, a reference that starts with `:`, and a null reference each yield no
identity. This fallback SHALL ensure a legacy collector's runs are still
grouped to it, so its latest verdict is attributed correctly rather than the collector
appearing to have no evidence. A run with no recoverable collector identity SHALL NOT
produce a collector status.

A pre-migration run records no cadence, so its `frequency` is null and it SHALL never be
`Stale` - its latest verdict (`HardFailure`, `SoftFailure`, or `Passing`) still shows.
Any run whose recorded cadence is null (every pre-migration run, every attestation run) or
yields no window SHALL never be `Stale`. Staleness is therefore forward-only: it applies
to runs collected after the migration ships, and a collector that had already stopped
keeps its last verdict, an accepted, bounded limitation of not backfilling.

The run-overall `result` SHALL draw from the closed set `{Pass, Fail, Error}` and each
per-check `result` SHALL draw from the closed set `{Pass, Fail}`; "failing" means a check
with `result == Fail`.

The status SHALL be derived over an ASSESSED SET of runs rather than over a single run. The
store SHALL pin the group's latest run by `ORDER BY collected_at DESC, received_at DESC,
created_at DESC, id DESC` (the ULID `id` is monotonic and gives a total-order tie-break).
When that pinned run carries a non-null `cycle_id`, the assessed set SHALL be every run in
the group carrying that same `cycle_id`, so one collection cycle is assessed as one
outcome across every machine it covered. When the pinned run carries a null `cycle_id`, the
assessed set SHALL be that pinned run alone.

Over the assessed set, evaluated against the current time obtained from an injected
`TimeProvider`, the precedence (most severe first) SHALL be: any run with a `Hard` check
whose `result == Fail` yields `HardFailure`; otherwise any run whose `result == Error`
yields `Errored`; otherwise any run that is stale (its `collected_at` is older than the
window plus grace derived from its recorded `frequency`, per the collector staleness window
evaluation) yields `Stale`; otherwise any run with a `Soft` check whose `result == Fail`
yields `SoftFailure`; otherwise `Passing`. A run whose recorded cadence yields no window is
never `Stale`.

`Errored` is the one status read from the run-overall `result` column rather than from the
run's checks, because an errored run has no checks to derive from. An `Error` run SHALL
NEVER be counted as `Passing`, and it SHALL be reported as `Errored` rather than as `Stale`,
because "the collection attempt failed" is a sharper and fresher statement than "the last
collection is overdue".

`HardFailure` outranks `Errored` because a known hard failure is actionable and is never a
false green, while an error means the answer is not known. This holds both when the failing
check and the error come from different runs of one cycle and when one partially collected
run carries both: the failing check WAS observed, and only the unreached part of the
collection is unknown. Reporting `Errored` over an observed hard failure would show an
amber degraded state for a confirmed policy breach. `Errored` outranks `Stale` for the same
reason `Stale` outranks `Passing`: it is the more specific description of why the verdict
cannot be trusted.

Grouping by collector SHALL ensure a stopped collector is not masked by a different
collector that produced fresher evidence for the same requirement. Assessing a whole cycle
SHALL ensure one failing or errored machine is not masked by a passing machine in the same
cycle, and SHALL ensure a machine that no longer appears in the newest cycle stops
contributing, so a retired machine cannot pin a collector to `Stale` forever.

Wherever the derivation compares a run's `result`, a run's `kind`, or a check's `severity`
and `result`, the comparison SHALL accept exactly the values an ordinal comparison accepts,
so the assessed outcome does not depend on the server's default collation. Case, accent, and
a trailing space are all significant.

The status derivation SHALL read only the evidence tables: the assessed runs'
`collected_at`, `collector_id`, `cycle_id`, `frequency`, and `result` are columns on
`evidence_runs` and their checks are rows in `evidence_checks`, so the derivation does NOT
read scopes, assets, or collector configuration. The pin, the assessed set, and the checks
SHALL be read as one consistent snapshot, so an append cannot land between the pin and the
checks assessed with it.

The store SHALL return a status only for a collector that has a run and SHALL NOT emit an
`Unknown` status. Deriving `Unknown` for a configured (expected) collector that has no
store row is the caller's responsibility; `Unknown` is a distinct status from `Stale` and
from `Errored` (`Stale` means prior collector evidence exists but has gone overdue,
`Errored` means the latest collection attempt failed, `Unknown` means the collector never
produced evidence).

#### Scenario: A collector's latest run with a failing hard check assesses as HardFailure

- **WHEN** the latest run for an `(organisation, requirement, collector)` has a failing
  `Hard` check
- **THEN** the computed status for that collector is `HardFailure`

#### Scenario: An errored latest run assesses as Errored, not Passing and not Stale

- **WHEN** a collector's latest run has `result` = `Error`
- **THEN** the computed status for that collector is `Errored`, distinct from `Passing`,
  from `Stale`, and from `HardFailure`

#### Scenario: A hard failure outranks an error

- **WHEN** one run in a collector's assessed set has a failing `Hard` check and another has
  `result` = `Error`
- **THEN** the computed status is `HardFailure`, because a known hard failure outranks a
  collection that could not determine the answer

#### Scenario: A hard failure outranks staleness

- **WHEN** a collector's latest run has a failing `Hard` check and is also older than its
  window plus grace
- **THEN** the computed status is `HardFailure`, because a known hard failure is the most
  severe status and outranks `Stale`

#### Scenario: An error outranks staleness

- **WHEN** a collector's latest run has `result` = `Error` and is also older than its
  window plus grace
- **THEN** the computed status is `Errored`, not `Stale`

#### Scenario: An overdue passing collector assesses as Stale, not Passing

- **WHEN** a collector's latest run has no failing check but was collected longer ago
  than the window plus grace derived from its recorded `daily` cadence
- **THEN** the computed status for that collector is `Stale`, not `Passing`, so a stopped
  collector is not shown as a false green

#### Scenario: One failing machine in a cycle is not hidden by its passing siblings

- **WHEN** one collection cycle appended a failing run for one machine and passing runs for
  every other machine under the same organisation, requirement, and collector
- **THEN** the computed status for that collector is the failing status, because the whole
  cycle is the assessed set

#### Scenario: One errored machine in a cycle is not hidden by its passing siblings

- **WHEN** one collection cycle appended an `Error` run for one machine and passing runs
  for every other machine under the same organisation, requirement, and collector
- **THEN** the computed status for that collector is `Errored`

#### Scenario: A machine absent from the newest cycle stops contributing

- **WHEN** a machine that had runs in an earlier cycle has no run in the newest cycle for
  the same organisation, requirement, and collector
- **THEN** that machine's earlier run does not contribute to the computed status, because
  the assessed set is the newest cycle only

#### Scenario: A check value in the wrong case does not change the derivation

- **WHEN** a writer that bypasses the application records a check whose `severity` is `hard`
  and whose `result` is `fail` on an otherwise passing run
- **THEN** the derived status is unchanged, because the derivation compares `Hard` and `Fail`
  under a binary collation, matching the ordinal comparison the application uses

#### Scenario: A padded check value does not change the derivation either

- **WHEN** a writer that bypasses the application records a check whose `severity` is `Hard `
  and whose `result` is `Fail `, each with a trailing space, on an otherwise passing run
- **THEN** the derived status is unchanged, because the derivation's collation is NO PAD and
  the ordinal comparison does not match a padded value

#### Scenario: A run with no cycle is assessed alone

- **WHEN** a collector's latest run carries a null `cycle_id`
- **THEN** the assessed set is that one run and the computed status is derived from it
  alone, unchanged from the behaviour before the machine dimension was added

#### Scenario: A stale collector is not masked by a fresh sibling collector

- **WHEN** two collectors verify the same requirement, one collector's latest run is
  overdue for its cadence, and the other collector's latest run is fresh and passing
- **THEN** the overdue collector's status is `Stale` and the fresh collector's status is
  `Passing`, because derivation is per collector rather than per the requirement's single
  latest run

#### Scenario: A collector's latest run with only a failing soft check assesses as SoftFailure

- **WHEN** a collector's latest run has no failing `Hard` check, did not error, is not
  overdue, but has a failing `Soft` check
- **THEN** the computed status for that collector is `SoftFailure`

#### Scenario: A collector with no evidence run has no store status

- **WHEN** a collector configured to verify a requirement has no evidence run for an
  organisation
- **THEN** the read store returns no status for that collector; the caller derives
  `Unknown` for it, distinct from `Stale` and from `Errored`

#### Scenario: A pre-migration run is attributed by its collector_ref prefix but is never stale

- **WHEN** a collector-kind run has a null `collector_id`, a null `frequency`, and a
  `collector_ref` of the form `collector_id:run_id`
- **THEN** the run is attributed to the collector named by the `collector_ref` prefix and
  contributes its verdict to that collector's status, and the collector is never `Stale`
  because the run carries no cadence

#### Scenario: A superseded earlier run does not decide the status

- **WHEN** an earlier run for a collector had a `Hard` check with `result` = `Fail` but a
  later run for the same collector (higher `collected_at`) has all checks `Pass`
- **THEN** the computed status for that collector is derived from the assessed set pinned by
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
  migrations, and exercise the append, both idempotency keys, immutability, the machine
  dimension, the `Error` result, and assessment-derivation behaviour

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
There SHALL be NO foreign key from `collector_id` to the collector table, before or after
the collector merge, so a recorded run survives deletion or gitops churn of the collector,
consistent with the scalar `organisation_id`/`requirement_id` columns; the collector-merge
migration SHALL NOT add one.

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

#### Scenario: The collector merge adds no foreign key to evidence runs

- **WHEN** the collector-merge migration completes
- **THEN** `evidence_runs.collector_id` still carries no foreign key, so a run whose
  collector was since deleted is retained and its collector delete is never blocked

### Requirement: Migration adds the machine dimension, the collection cycle, and the error result to evidence runs

A forward-only migration applied by `freeboard system migrate` SHALL add to the existing
`evidence_runs` table: a nullable `asset_id VARCHAR(190)` column with `utf8mb4_bin`
collation naming the machine asset a run describes; a nullable `cycle_id CHAR(26)` column
with `utf8mb4_bin` collation recording the collection cycle the run belongs to; a nullable
`error_detail TEXT` column carrying why an errored collection failed; and a generated
`asset_key VARCHAR(190)` column with `utf8mb4_bin` collation defined as
`COALESCE(asset_id, _utf8mb4'')`. There SHALL be NO foreign key from `asset_id` to the `assets`
table, so a recorded run survives retirement or deletion of the machine, consistent with
the scalar `organisation_id`, `requirement_id`, and `collector_id` columns.

The migration SHALL relax `vendor` and `collector_ref` to nullable, SHALL KEEP the existing
`UNIQUE (vendor, collector_ref)` key unconditional and unchanged, SHALL add the unique
key `uq_evidence_runs_cycle (cycle_id, organisation_id, requirement_id, asset_key)`, and
SHALL add four enforced check constraints:
`ck_evidence_runs_result` requiring `result IN ('Pass', 'Fail', 'Error')`;
`ck_evidence_runs_error_detail` requiring an `error_detail` that is non-null and holds at
least one non-whitespace character when `result` is `Error`, and a null `error_detail`
otherwise;
`ck_evidence_runs_ref_pair` requiring
`vendor` and `collector_ref` to be both null or both non-null; and
`ck_evidence_runs_cycle_identity` requiring EXACTLY ONE of the two identities - either a
non-null `vendor` with a non-null `collector_ref` and a null `cycle_id`, or a NON-BLANK
`collector_id` with a NON-BLANK `cycle_id` and a null `vendor` and `collector_ref`. The cycle
arm tests for a non-blank value rather than a non-null one because an empty `collector_id`
names no collector to the read side, so a row carrying one would store and then contribute to
no collector's status. A non-blank test SHALL reject any run of whitespace, not only a run of
spaces, so the database agrees with the store about which values name nothing.

Every comparison of an enum-like evidence value in SQL SHALL accept exactly the values an
ordinal comparison accepts. `evidence_runs.result`, `evidence_runs.kind`,
`evidence_checks.severity`, and `evidence_checks.result` carry no explicit collation, so they
inherit the server default, which on MySQL 8.4 is `utf8mb4_0900_ai_ci`. A comparison against a
bare literal would therefore accept `error` for `Error` and `fail` for `Fail`. The application
compares these values with an ordinal, case-sensitive comparison, so SQL that did otherwise
would disagree with the code it replaces, and `ck_evidence_runs_result` would admit a value
the derivation does not recognize. Each such comparison SHALL therefore declare `COLLATE
utf8mb4_0900_bin` on the compared literal, written with the `_utf8mb4` introducer so the
literal's character set does not follow the client that ran the statement. This SHALL hold for
the check constraints the migration adds and for the predicates of the status query.

The collation SHALL be `utf8mb4_0900_bin` rather than `utf8mb4_bin`. Both are binary, but
`utf8mb4_bin` is a PAD SPACE collation, so trailing spaces are not significant and it would
still accept `Error ` for `Error`. `utf8mb4_0900_bin` is NO PAD, so a trailing space is
significant, exactly as it is in the ordinal comparison the SQL has to match.

The two identity constraints exist because MySQL does not dedup on a null key part. Without
`ck_evidence_runs_ref_pair`, a run carrying only one half of the legacy pair would escape the
`(vendor, collector_ref)` key. Without `ck_evidence_runs_cycle_identity`, a run carrying
neither identity would escape both keys, and a run carrying both would be admitted by both,
which would make the two keys contend on one row.

The generated column is required rather than indexing `asset_id` directly, because MySQL
treats each `NULL` in a unique index as distinct and an organisation-level run carries a
null `asset_id`. The unique key SHALL NOT carry `collector_id`: a `cycle_id` is a ULID
minted against one collector, so the column is redundant, and a fifth `VARCHAR(190)` part
would take the key past the InnoDB 3072-byte index limit.

The migration SHALL NOT add an index on `asset_id`. The existing
`(organisation_id, requirement_id, collected_at)` index serves the reads this change
touches through its leftmost prefix, and a further index would only add write cost to an
append-hot table.

The migration SHALL be purely additive: one multi-clause `ALTER TABLE` so the whole shape
change applies or rolls back as a unit. It SHALL NOT backfill any row, SHALL NOT run any
`UPDATE` against `evidence_runs`, and SHALL NOT drop or re-create any of the six
append-only triggers, so the append-only guarantee holds throughout and after the
migration. Pre-migration runs SHALL read back null for `asset_id`, `cycle_id`, and
`error_detail`, SHALL keep their `vendor` and `collector_ref`, and SHALL keep the derived
status they had before the migration.

Because the migration runner runs the migration SQL and only then records the version, and
plain `ADD COLUMN` is not idempotent, the migration is NOT atomically replay-safe. The
migration header SHALL document the operational recovery: drop the added columns, the added
unique key, and the added check constraints and re-run, or record the migration version by
hand.

The header SHALL also state the cost of applying it, measured rather than assumed. Adding a
unique key over a generated column, and modifying two columns an existing unique key covers,
are not metadata-only changes. The server may serve them with `ALGORITHM=INPLACE` or it may
fall back to `ALGORITHM=COPY`, and a copy BLOCKS concurrent writes to `evidence_runs` for its
whole duration, including every HTTP evidence ingest. The header SHALL record which algorithm
a real MySQL 8.4 chose for the statement that shipped, and SHALL state whether concurrent
writes are blocked while it runs, not only how long it takes. The header SHALL also state that
`uq_evidence_runs_cycle` costs an index write on every later insert, including every insert
of a run that carries no cycle. This code SHALL live in
the MIT `Freeboard.Persistence` project and SHALL NOT add any reference to
`Freeboard.Enterprise` or any new dependency.

#### Scenario: Migration adds the machine, cycle, detail, and generated columns

- **WHEN** the migration runs against a database with the evidence migrations applied
- **THEN** `evidence_runs` carries nullable `asset_id`, `cycle_id`, and `error_detail`
  columns and a generated `asset_key` column
- **AND** `vendor` and `collector_ref` are nullable

#### Scenario: The cycle idempotency key and the check constraints exist

- **WHEN** the migration runs
- **THEN** a unique key over `(cycle_id, organisation_id, requirement_id, asset_key)`
  exists, the pre-existing `(vendor, collector_ref)` unique key still exists, and the
  `ck_evidence_runs_result`, `ck_evidence_runs_error_detail`, `ck_evidence_runs_ref_pair`,
  and `ck_evidence_runs_cycle_identity` constraints reject a row that breaks them

#### Scenario: The closed result set is case-sensitive

- **WHEN** a writer that bypasses the application inserts a run whose `result` is `error`
  rather than `Error`
- **THEN** `ck_evidence_runs_result` rejects the row, because the constraint compares against
  a binary-collated literal and the derivation recognizes only `Error`

#### Scenario: A trailing space is not the closed-set value

- **WHEN** a writer that bypasses the application inserts a run whose `result` is `Error `
  with a trailing space
- **THEN** `ck_evidence_runs_result` rejects the row, because the constraint's collation is NO
  PAD and the derivation's ordinal comparison does not match that value either

#### Scenario: A pre-migration run keeps its values and its status

- **WHEN** the migration runs and a pre-migration collector run already exists
- **THEN** the run's new `asset_id`, `cycle_id`, and `error_detail` columns are all null,
  its `asset_key` is the empty string, its `vendor`, `collector_ref`, `result`,
  `collected_at`, and checks are unchanged, and its collector's derived status is what it
  was before the migration

#### Scenario: The append-only guards are untouched by the migration

- **WHEN** the migration runs
- **THEN** all six `BEFORE UPDATE` and `BEFORE DELETE` triggers on `evidence_runs`,
  `evidence_checks`, and `attestation_responses` still exist, so a stray UPDATE or DELETE
  remains rejected throughout and after the migration

#### Scenario: The machine reference does not block a machine delete

- **WHEN** a machine asset that an evidence run names in its `asset_id` is deleted
- **THEN** the delete is not blocked and the run remains, because `asset_id` is a scalar
  column with no foreign key

