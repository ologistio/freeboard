## Why

Freeboard persists the compliance *definition* domain (standards, requirements,
organisations, scopes) but has no place to store the *runtime evidence* that a
requirement is actually met. Collector runs, attestation questionnaires, and the
derived pass/fail status they imply are produced at runtime and today have
nowhere to live. Without an evidence store the product cannot show whether an
in-scope requirement is satisfied. This change adds the MySQL schema, migration,
and read/append stores for that runtime state so later work can collect evidence
and render assessment status.

## What Changes

- Add migration `011_evidence.sql` (applied via `freeboard system migrate`)
  creating three tables: `evidence_runs`, `evidence_checks`, and
  `attestation_responses`.
- Model an **Evidence run** as one immutable collector observation: a per-run
  idempotency key (`collector_ref`), vendor, timestamp, overall result, opaque
  raw payload, and a set of nested named checks, each carrying a severity
  (`Hard` / `Soft`) and a result. A `UNIQUE (vendor, collector_ref)` key makes a
  re-delivered observation idempotent.
- Model an **AttestationResponse** as a specialised Evidence run (discriminator
  `kind = AttestationResponse`) with a 1:1 extension row holding the respondent
  and quiz outcome; its per-question answers reuse the same nested-check rows,
  so the assessment logic consumes attestation and collector evidence through one
  check model.
- Derive **AssessmentResult** (per organisation/requirement status) computed-on-read
  from the latest evidence run and its checks. No stored assessment table. The
  store returns a status (`HardFailure` / `SoftFailure` / `Passing`) only for pairs
  that have evidence; `NoEvidence` is derived by the web caller for an in-scope pair
  with no store row (the web project owns in-scope enumeration / SoA scope
  resolution). The status is deliberately named for what the evidence shows, not
  "Compliant": it means "the latest run has no failing hard check", not "the
  requirement is satisfied". Overall run and per-check `result` use the closed set
  `{Pass, Fail}`, validated as strings in the write store.
- Enforce Evidence append-only: the write store exposes only an append path (no
  update/delete method), backed by database `BEFORE UPDATE` / `BEFORE DELETE`
  triggers that reject any in-place mutation of a recorded run.
- Add read store `IEvidenceStore` and append store `IEvidenceWriteStore` with
  MySQL implementations, plus DI extensions, mirroring the existing compliance
  store split.
- Add MySQL integration tests gated on `FREEBOARD_TEST_DB` that skip cleanly when
  unset.

The external references (`organisation_id`, `requirement_id`, `user_id`) use
scalar id columns with no strict FK, following `authz_audit_events`. The mediator
selected this (Option A) over strict FK plus a GitOps importer preflight; see
design.md "Resolved decision: external FK strategy".

No public HTTP surface, no CLI command, and no web page are added here; this is
the persistence layer only.

## Capabilities

### New Capabilities

- `evidence-persistence`: MySQL-backed, append-only storage for Evidence runs and
  their nested checks, AttestationResponses as specialised Evidence, and the
  computed-on-read AssessmentResult projection, with migrations applied through
  `freeboard system migrate`.

### Modified Capabilities

None. This is additive: no existing requirement changes.

## Impact

- **Licensing**: MIT. All code lands in `src/Freeboard.Persistence` (MIT) and its
  test project. No `Freeboard.Enterprise` code, references, or carve-out. Evidence
  storage is core community capability, not a paid enterprise feature.
- **Code**: new migration `src/Freeboard.Persistence/Migrations/011_evidence.sql`;
  new files `EvidenceReadModels.cs`, `IEvidenceStore.cs`, `IEvidenceWriteStore.cs`,
  `MySqlEvidenceStore.cs`, `MySqlEvidenceWriteStore.cs`; edit to
  `PersistenceServiceCollectionExtensions.cs`; new integration test file under
  `tests/Freeboard.Persistence.Tests`.
- **Dependencies**: none added. Reuses Dapper, MySqlConnector, and the existing
  `IUlidFactory` seam.
- **Reference graph**: `Freeboard.Persistence -> Freeboard.Core` only, unchanged.
  Core, Agent, and CLI are untouched and stay EE-free and cross-platform.
- **Importer**: with scalar external refs and no FK (Option A) the GitOps importer
  (`MySqlGitOpsImporter`) is untouched.
- **Runtime**: migration 011 makes idempotent triggers with `DROP TRIGGER IF EXISTS`
  followed by a plain `CREATE TRIGGER` (no `IF NOT EXISTS`, no version floor beyond
  MySQL 8.0). It is the first migration to run `CREATE TRIGGER`, so on a stock
  binary-logging MySQL 8.x server the migration database user must either run against
  a server with `log_bin_trust_function_creators=1` or hold a privilege sufficient to
  create triggers under binary logging; otherwise the server rejects the create with
  error 1419 (`ER_BINLOG_CREATE_ROUTINE_NEED_SUPER`). The migration runner surfaces
  1419 with a message naming this remediation. The dev and CI compose file already
  sets `--log-bin-trust-function-creators=1` on MySQL 8.4.
