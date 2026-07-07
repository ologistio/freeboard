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
that identifies the specific observation; a non-null `vendor` source string; an
overall `result` (`Pass` or `Fail`); the `collected_at` timestamp of the
observation; an optional opaque `raw_payload` stored as JSON; and a `created_at`
insert timestamp. The `organisation_id`, `requirement_id`, and (for attestations)
`user_id` SHALL reference `organisations(id)`, `requirements(id)`, and `users(id)`
respectively. The default strategy is scalar id columns WITHOUT a strict foreign
key, following the `authz_audit_events` precedent, so a recorded run survives
deletion or gitops churn of the referenced organisation, requirement, or user.
Both `vendor` and `collector_ref` SHALL be `NOT NULL` so that re-delivery of the
same observation is rejected by the `UNIQUE (vendor, collector_ref)` key (MySQL
treats `NULL` as distinct, so a nullable column would not dedup).

#### Scenario: A collector run is appended with its identifying fields

- **WHEN** a collector run is appended for an organisation and requirement with a
  collector ref, vendor, result, timestamp, and raw payload
- **THEN** one `evidence_runs` row exists carrying all those fields with `kind` =
  `Collector`

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

The system SHALL provide a read store that returns evidence runs (with their
nested checks resolved) for an organisation and requirement, and that computes an
AssessmentResult status for each `(organisation, requirement)` pair that HAS at
least one evidence run, from that pair's latest run and its checks. The status
SHALL be derived on read (no stored assessment table) and SHALL describe the
evidence, not compliance: it means "the latest run has no failing hard check", not
"the requirement is satisfied".

Both the run-overall `result` and each per-check `result` SHALL draw from the
closed set `{Pass, Fail}`; "failing" means `result == Fail`. Derivation over the
latest run's checks: any `Hard` check with `result == Fail` yields `HardFailure`;
otherwise any `Soft` check with `result == Fail` yields `SoftFailure`; otherwise
`Passing`.

The latest run for a pair SHALL be pinned deterministically by
`ORDER BY collected_at DESC, received_at DESC, created_at DESC, id DESC` (the ULID
`id` is monotonic and gives a total-order tie-break), supported by the
`evidence_runs` index `(organisation_id, requirement_id, collected_at)`.

The store SHALL NOT enumerate the in-scope set and SHALL NOT emit `NoEvidence`.
Enumerating in-scope pairs, deriving `NoEvidence` for an in-scope pair that has no
store row, and intersecting with the resolved in-scope set are the caller's
responsibility (the web project owns SoA scope resolution). `NoEvidence` remains a
documented AssessmentResult status that the caller derives.

#### Scenario: Latest run with a failing hard check assesses as HardFailure

- **WHEN** the latest run for an `(organisation, requirement)` pair that has
  evidence has a failing `Hard` check
- **THEN** the computed AssessmentResult for that pair is `HardFailure` (the store
  derives status over pairs that have evidence; scope filtering is the caller's
  concern)

#### Scenario: Latest run with only a failing soft check assesses as SoftFailure

- **WHEN** the latest run for an `(organisation, requirement)` pair that has
  evidence has no failing `Hard` check but a failing `Soft` check
- **THEN** the computed AssessmentResult for that pair is `SoftFailure`

#### Scenario: A pair with no evidence run has no store status

- **WHEN** an organisation/requirement pair has no evidence run
- **THEN** the read store returns no AssessmentResult status for that pair; the web
  caller derives `NoEvidence` for it when the pair is in scope

#### Scenario: Only the latest run counts

- **WHEN** an earlier run for a pair had a `Hard` check with `result` = `Fail` but
  a later run for the same pair (higher `collected_at`) has all checks `Pass`
- **THEN** the computed AssessmentResult for that pair is `Passing`, derived from
  the latest run pinned by `collected_at`, `received_at`, `created_at`, `id`
  descending, and the earlier run's rows are unchanged

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

