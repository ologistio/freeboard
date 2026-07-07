## 1. Schema and migration

Commit: `feat(persistence): add evidence schema migration 011`

- [x] 1.1 Add `src/Freeboard.Persistence/Migrations/011_evidence.sql` creating the
  `evidence_runs` table: `id CHAR(26) utf8mb4_bin` PK; `kind VARCHAR(32) NOT NULL`
  (`Collector` / `AttestationResponse`); `organisation_id` and `requirement_id`
  `VARCHAR(190) utf8mb4_bin NOT NULL`; `collector_ref VARCHAR(190) CHARACTER SET
  utf8mb4 COLLATE utf8mb4_bin NOT NULL`; `vendor VARCHAR(190) CHARACTER SET utf8mb4
  COLLATE utf8mb4_bin NOT NULL`; `result VARCHAR(32) NOT NULL` (`Pass` / `Fail`);
  `collected_at DATETIME(6) NOT NULL`; `received_at DATETIME(6) NULL`;
  `raw_payload JSON NULL`; `created_at DATETIME(6) NOT NULL`. Add
  `UNIQUE (vendor, collector_ref)` (both columns `NOT NULL` and `utf8mb4_bin` so
  the unique key dedups by exact case-sensitive bytes; MySQL treats `NULL` as
  distinct), and indexes on `(organisation_id, requirement_id, collected_at)` and
  `(requirement_id)`. Use `CREATE TABLE IF NOT EXISTS`. The external refs
  (`organisation_id`, `requirement_id`) are scalar columns with no FK (Option A),
  indexed for the read path.
- [x] 1.2 In the same migration create `evidence_checks`: `id CHAR(26)
  utf8mb4_bin` PK; `evidence_id CHAR(26) utf8mb4_bin NOT NULL` FK to
  `evidence_runs(id)`; `name VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
  NOT NULL`; `severity VARCHAR(16) NOT NULL` (`Hard` / `Soft`);
  `result VARCHAR(32) NOT NULL`; `ordinal INT NOT NULL`; `detail TEXT NULL`;
  `UNIQUE (evidence_id, name)` (case-sensitive exact match via `utf8mb4_bin`).
  No separate `(evidence_id)` index: `UNIQUE (evidence_id, name)` covers the
  `evidence_id` FK lookup via its leftmost prefix.
- [x] 1.3 In the same migration create `attestation_responses`:
  `evidence_id CHAR(26) utf8mb4_bin` PK (NOT NULL) and FK to `evidence_runs(id)`;
  `user_id CHAR(26) utf8mb4_bin NOT NULL`; `quiz_passed TINYINT(1) NOT NULL`;
  `score INT NULL`; index on `(user_id)`. `user_id` is a scalar column with no FK
  (Option A), indexed for the read path.
- [x] 1.4 In the same migration add `BEFORE UPDATE` and `BEFORE DELETE` triggers on
  all three tables, each a single-statement `SIGNAL SQLSTATE '45000' SET
  MESSAGE_TEXT = 'evidence is append-only'`, created idempotently (`CREATE TRIGGER
  IF NOT EXISTS`, or `DROP TRIGGER IF EXISTS` before each `CREATE TRIGGER`; no
  `BEGIN ... END` body). Add a header comment explaining append-only, the
  external-ref strategy, the idempotency key, and the idempotent-replay
  requirement, matching the style of migrations 007-010.

## 2. Read models and store interfaces

Commit: `feat(persistence): add evidence read and append store interfaces`

- [x] 2.1 Add `src/Freeboard.Persistence/EvidenceReadModels.cs` with records
  `EvidenceRunRow`, `EvidenceCheckRow`, `AttestationResponseRow`, and
  `AssessmentResultRow` (status one of `HardFailure` / `SoftFailure` / `Passing` /
  `NoEvidence`), mirroring the record and XML-doc conventions in
  `ComplianceReadModels.cs`. No new `Freeboard.Core` types.
- [x] 2.2 Add `IEvidenceStore` (read): get evidence runs with resolved checks for
  an `(organisation, requirement)`; get the latest run for a pair; get computed
  `AssessmentResultRow`s for an organisation. Document that assessment is derived
  on read and means "latest run has no failing hard check", not "requirement
  satisfied".
- [x] 2.3 Add `IEvidenceWriteStore` (append only): `AppendEvidenceAsync` (run +
  checks) and `AppendAttestationResponseAsync` (run + checks + extension). No
  update or delete method. Return `WriteResult` for validation failures and for a
  duplicate `(vendor, collector_ref)`, reusing the existing `WriteResult` type.

## 3. MySQL store implementations

Commit: `feat(persistence): add mysql evidence stores`

- [x] 3.1 Implement `MySqlEvidenceWriteStore` using Dapper in one transaction per
  append: validate `kind` in `Collector`/`AttestationResponse`, `severity` in
  `Hard`/`Soft`, the run-overall `result` and each per-check `result` in the closed
  set `{Pass, Fail}`, non-null `vendor` and `collector_ref`, and non-empty ids;
  generate ULIDs via `IUlidFactory`; insert the `evidence_runs` row, then its
  `evidence_checks`, then (for attestations) the `attestation_responses` row.
- [x] 3.2 Use plain `INSERT` statements only (never `INSERT ... ON DUPLICATE KEY
  UPDATE`, `REPLACE INTO`, or `INSERT IGNORE`: the first two trip the
  `BEFORE UPDATE` / `BEFORE DELETE` triggers, and `IGNORE` silently swallows the
  collision). Catch a duplicate `(evidence_id, name)` or `(vendor, collector_ref)`
  and map it to a failing `WriteResult`, rolling back so no partial run is left
  behind (rolling back uncommitted `INSERT`s does not fire the delete trigger).
- [x] 3.3 Implement `MySqlEvidenceStore` with hand-written joined reads
  (evidence run + resolved checks ordered deterministically by `ordinal`), and the
  AssessmentResult projection computed under a `RepeatableRead` snapshot from
  `evidence_runs` and `evidence_checks` only, returning a status for each pair that
  has evidence. Pin each pair's latest run by `ORDER BY collected_at DESC,
  received_at DESC, created_at DESC, id DESC`. Derive over the latest run's checks
  (result in `{Pass, Fail}`, `Fail` = failing): any `Hard` `Fail` => `HardFailure`;
  else any `Soft` `Fail` => `SoftFailure`; else `Passing`. Do NOT read `scopes` /
  `requirement_scopes`, do NOT enumerate in-scope pairs, and do NOT emit
  `NoEvidence` - the web caller owns SoA scope resolution and derives `NoEvidence`.
- [x] 3.4 Add DI extensions `AddEvidenceStore` and `AddEvidenceWriteStore` to
  `PersistenceServiceCollectionExtensions.cs`, registering the shared connection
  factory and `TryAddSingleton<IUlidFactory, UlidFactory>()`, mirroring
  `AddComplianceStore` / `AddComplianceWriteStore`. No `MySqlGitOpsImporter`
  change: under Option A the importer is untouched (scalar refs, no FK to block a
  prune).

## 4. Integration tests

Commit: `test(persistence): add evidence integration tests`

- [x] 4.1 Add `tests/Freeboard.Persistence.Tests/EvidenceIntegrationTests.cs`
  using `MySqlTestDatabase` and `[RequiresEnvVarFact(EnvVar =
  MySqlTestDatabase.EnvVar)]`, applying migrations via `MySqlMigrationRunner`,
  matching `AuthzIntegrationTests` structure.
- [x] 4.2 Cover schema and immutability: migration applies and the three tables,
  the `UNIQUE (vendor, collector_ref)` key, and the triggers exist; an append
  persists an evidence run with its checks; an attestation append persists the
  extension row and per-question checks; a duplicate check name fails; a duplicate
  `(vendor, collector_ref)` fails; a raw UPDATE and a raw DELETE against each table
  are rejected by the trigger. Also assert evidence survives deletion of its
  referenced requirement/organisation (Option A: scalar refs, no FK).
- [x] 4.3 Cover AssessmentResult derivation over pairs that have evidence: a
  `Hard` check with `result` = `Fail` => `HardFailure`; only a `Soft` `Fail` =>
  `SoftFailure`; all checks `Pass` => `Passing`; a pair with no run returns no
  store status (the store does not emit `NoEvidence`); a later run changes the
  computed result while the earlier run's rows stay unchanged (only the latest run
  counts, pinned by `collected_at`, `received_at`, `created_at`, `id` descending).

- [x] 4.4 Cover idempotent replay of the 011 SQL directly. `ApplyPendingAsync`
  records applied migrations per file and will not re-run a recorded migration, so
  the normal apply test does not exercise replay. Execute the raw 011 SQL text
  against a throwaway database TWICE (not via `ApplyPendingAsync`) and assert the
  second execution succeeds, proving `CREATE TABLE ... IF NOT EXISTS` and
  `CREATE TRIGGER IF NOT EXISTS` (or `DROP TRIGGER IF EXISTS` before each
  `CREATE TRIGGER`) make the migration re-runnable.

## 5. Verification

Commit: (folded into the commit that completes the work)

- [x] 5.1 Run `dotnet build` (asserts the reference graph and that Persistence
  gains no EE reference).
- [x] 5.2 Run `dotnet test` with `FREEBOARD_TEST_DB` unset and confirm the new
  tests skip cleanly.
- [x] 5.3 Run `dotnet test` against local MySQL 8.4 with `FREEBOARD_TEST_DB` set
  and confirm the evidence integration tests pass.
- [x] 5.4 Run `npx markdownlint-cli2 "**/*.md"` for any Markdown touched.
