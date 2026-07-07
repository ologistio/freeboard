## Context

This design synthesises two independent plans for the same task:

- **Plan A (Planner)**: the original OpenSpec artifacts in this change directory.
- **Plan B (Codex)**: an independent plan proposing `evidence_runs`, Core domain
  enums, strict external FKs with an importer preflight, and a dedicated
  `attestation_response_answers` table.

Where the two diverge, the resolution and its rationale are recorded inline in
each Decision below, tagged with the source of each idea. One divergence - the
external-FK strategy - was escalated to a mediator, who selected Option A on the
plan-phase review; it is recorded under "Resolved decision: external FK
strategy".

The compliance definition domain is already persisted (migrations 007-010):
`organisations`, `standards`, `requirements`, `scopes` (organisation x standard
disposition), and `requirement_scopes` (organisation x requirement disposition).
All ids and FK columns are `VARCHAR(190) CHARACTER SET utf8mb4 COLLATE
utf8mb4_bin` to match Core's exact-byte id identity. Runtime-generated ids
elsewhere (auth, authz audit) are ULIDs stored as `CHAR(26) ... utf8mb4_bin` via
the `IUlidFactory` seam.

What is missing is runtime *evidence*: the dynamic state a collector or an
attestation questionnaire produces to show whether an in-scope requirement is
met. This design adds that layer. Established patterns this change follows:

- Migrations are hand-written, forward-only, embedded `Migrations/NNN_*.sql`,
  applied by `MySqlMigrationRunner` (whole file executed as one batch; the
  runner replays a partially-failed migration, so every statement must be
  idempotent / re-runnable). Next ordinal is `011` (last is
  `010_authorization.sql`).
- Stores split read from write: `MySqlComplianceStore` (`IComplianceStore`) reads;
  `MySqlComplianceWriteStore` (`IComplianceWriteStore`) writes. Read-model records
  live in `Freeboard.Persistence` (`ComplianceReadModels.cs`), not Core. DI is
  role-split (`AddComplianceStore`, `AddComplianceWriteStore`, ...).
- Projections that combine several tables in one snapshot are computed-on-read
  under a `RepeatableRead` transaction (see
  `MySqlComplianceStore.GetStatementOfApplicabilityInputsAsync`).
- The `authz_audit_events` table (migration 010) records a durable append-only
  trail using *scalar* actor/resource id columns with **no** strict FKs, so the
  trail survives user and organisation deletes.
- Enum-ish string columns store the PascalCase enum *name*: `organisations.kind`
  holds `Company` / `Department` (`nameof(OrganisationKind.Company)`), and
  `scopes.disposition` holds `In` / `Out` (`nameof(ScopeDisposition.In)`),
  parsed case-sensitively by `ConfigValidator`.
- A JSON column is already used for opaque structured blobs
  (`webauthn_options JSON` in migration 002).

Repo constraints: `Freeboard.Persistence` references only `Freeboard.Core`; no EE
code or reference; ASCII punctuation; markdownlint; code-as-liability (prefer no
new Core types, no new dependency).

## Goals / Non-Goals

**Goals:**

- Schema + migration `011` for Evidence runs, their nested named checks, and
  AttestationResponse extension rows, applied via `freeboard system migrate`.
- Evidence is genuinely append-only: no in-place mutation of a recorded run,
  enforced both by the store surface (append-only API) and by the database.
- Idempotent re-delivery of an observation via `UNIQUE (vendor, collector_ref)`.
- A read store surfacing evidence, its checks, and a derived AssessmentResult
  status for each organisation/requirement pair that has evidence (the web caller
  owns in-scope enumeration and `NoEvidence` derivation; see Decision 3).
- MySQL integration tests gated on `FREEBOARD_TEST_DB` that skip cleanly.

**Non-Goals:**

- No collector runtime, agent integration, questionnaire engine, or scheduler.
- No web pages, HTTP API, or CLI command. Surfaces are read-only consumers added
  later; this change ships only the persistence layer and its interfaces.
- No evidence retention / garbage collection. Runs are permanent for now; a
  controlled purge, if ever needed, is a future migration (see Decision 4).
- No materialised AssessmentResult table (see Decision 3).
- No `collectors` or `vendors` reference tables; collector ref and vendor are
  recorded as scalar strings on the run (see Decision 5).
- No dedicated `attestation_response_answers` table (see Decision 2); per-question
  answers are `evidence_checks` rows, with the full submission in `raw_payload`.
- No in-scope enumeration / SoA scope resolution in `Freeboard.Persistence` - the
  web caller owns it (see Decision 3). The store returns a status only for pairs
  that have evidence.

## Divergences and resolutions

### Divergence: table name (`evidence` vs `evidence_runs`)

- **Plan A**: `evidence`.
- **Plan B**: `evidence_runs`.
- **Resolution**: `evidence_runs`. The existing schema names entity tables in the
  plural (`organisations`, `requirements`, `scopes`, `requirement_scopes`,
  `authz_audit_events`); `evidence` is a singular mass noun that breaks that
  convention. Both plans already describe the row as an "evidence run" in prose,
  and the child FK column reads naturally as `evidence_id` regardless. The child
  table stays `evidence_checks` and the 1:1 extension stays
  `attestation_responses`, both referencing `evidence_runs(id)` via an
  `evidence_id` column.

### Divergence: discriminator value casing

- **Plan A**: lowercase (`collector` / `attestation`).
- **Plan B**: PascalCase (`AttestationResponse`).
- **Resolution**: PascalCase, matching the repo's stored enum-name convention
  (`organisations.kind` = `Company`, `scopes.disposition` = `In`). `kind` values
  are `Collector` and `AttestationResponse`. `severity` values are `Hard` /
  `Soft`. Result and status vocabularies (Decision 3) likewise use PascalCase.

### Divergence: assessment status naming

- **Plan A**: `Pass` / `Warn` / `Fail` / `NotAssessed`.
- **Plan B**: `OutOfScope` / `NoEvidence` / `PassingEvidence` / `SoftFailure` /
  `HardFailure`, with an explicit caution (Codex H3) against naming any status
  "Compliant" or implying "requirement satisfied".
- **Resolution**: adopt the caution. The computed status describes the evidence,
  not compliance: it means "the latest run has no failing hard check", never
  "the requirement is satisfied" (there is no expected-check catalogue, so a pass
  can be overclaimed). Vocabulary: `HardFailure`, `SoftFailure`, `Passing`,
  `NoEvidence`. The store returns one status per pair that has evidence
  (`HardFailure` / `SoftFailure` / `Passing`); `NoEvidence` and `OutOfScope` are
  caller-derived (see Decision 3). `NoEvidence` is the web caller's status for an
  in-scope pair with no store row; `OutOfScope` is reserved for a future surface
  that computes over the full requirement catalogue.

### Divergence: per-question answer modelling

- **Plan A**: per-question answers reuse `evidence_checks` (each question is a
  named check with a severity and a result).
- **Plan B**: a dedicated `attestation_response_answers` table (PK
  `(evidence_id, ordinal)`, `question_ref`, `answer_payload JSON`, nullable
  `is_correct`), *plus* still writing `evidence_checks`.
- **Resolution**: reuse `evidence_checks` only (Plan A); defer the dedicated
  answers table. Both the AssessmentResult derivation and the answer display read
  checks: per-question correctness is the check `result`, the chosen-answer text
  fits `evidence_checks.detail`, and the full raw submission already lives in
  `evidence_runs.raw_payload` (JSON) for audit. Codex itself specifies that an
  attestation "still writes `evidence_checks` so assessment logic consumes both
  attestation and collector evidence through the same check model", so a second
  answers table is additive surface for indexed per-question querying that no
  planned surface yet needs. Under code-as-liability it is a Non-Goal here; if a
  surface later needs indexed answer querying it can be added behind the same read
  contract without changing callers.

### Divergence: idempotency key `UNIQUE (vendor, collector_ref)`

- **Plan A**: no uniqueness on `collector_ref`.
- **Plan B (M3)**: `UNIQUE (vendor, collector_ref)` for idempotent retries.
- **Resolution**: include it (Decision 5), with `collector_ref` defined as the
  vendor's stable id for **this specific observation/submission** (a per-run
  idempotency key), not an identifier of the collector source. So a collector
  that runs daily produces many rows with distinct `collector_ref` values
  (history is preserved and "only the latest run counts" still holds), while a
  re-delivery of the same observation collides and is rejected. Both `vendor` and
  `collector_ref` are `NOT NULL` so the unique index actually dedups (MySQL treats
  `NULL` as distinct, so a nullable column would not dedup). Attestations supply a
  stable non-null source string as `vendor` (the attestation/questionnaire system
  id) and their submission id as `collector_ref`. The write store also maps a
  duplicate to a `WriteResult` failure so callers see it explicitly.

### Divergence: Core domain enums vs Persistence-only strings

- **Plan A**: no new Core types; severity/result/status validated as strings in
  the persistence layer (`EvidenceReadModels.cs`, alongside
  `ComplianceReadModels.cs`).
- **Plan B**: add Core domain enums/records for evidence kind, severity, check
  result, quiz result, assessment status.
- **Resolution**: keep them in `Freeboard.Persistence` (Decision 6). Unlike the
  gitops config domain (`OrganisationKind`, `ScopeDisposition`) which lives in
  Core because Core owns config validation, evidence is runtime state with no
  Core-side producer or validator; adding Core enums now is speculative surface
  (code-as-liability). If a future web/CLI surface needs a shared status enum it
  can be introduced in Core then. Flagged in Open Questions.

## Decisions

### Decision 1: Nested checks as a child table; raw payload as JSON

`evidence_runs` (parent) holds the run identity; `evidence_checks` (child, one row
per named check) holds each check's `name`, `severity` (`Hard` / `Soft`),
`result`, and an `ordinal` - all `NOT NULL` - plus an optional (nullable) `detail`.
The vendor's opaque payload is a nullable `raw_payload JSON` column on
`evidence_runs`. Because rows are append-only and cannot be repaired in place,
every field the spec requires to be present is `NOT NULL` in the migration; only
genuinely optional fields are nullable (`evidence_runs.received_at`,
`evidence_runs.raw_payload`, `evidence_checks.detail`,
`attestation_responses.score`).

Rationale: the surfaces read checks per-check (list each named check with its
severity and result) and, critically, AssessmentResult is *derived from* the
checks (a failing `Hard` check fails the requirement; a failing `Soft` check
warns). Deriving that from JSON would force app-side parsing or MySQL JSON
functions and defeat indexing. A child table is queryable and aggregatable in
SQL (`GROUP BY evidence_id`). The raw payload, by contrast, is opaque and
display/audit-only, never filtered on, so a JSON column is the right shape for it
(matching `webauthn_options JSON`). Both plans agree on this shape.

### Decision 2: AttestationResponse = shared table + discriminator + 1:1 extension

`evidence_runs.kind` discriminates `Collector` from `AttestationResponse`.
Attestation-only fields live in a separate `attestation_responses` table keyed
1:1 on `evidence_id` (all `NOT NULL`: `evidence_id` PK, respondent `user_id`,
`quiz_passed`; only `score` is nullable/optional). The
per-question answers reuse `evidence_checks` (each question is a named check with
a severity and a result) - see the answer-modelling divergence above.

Rationale: an attestation *is* an evidence run (it has a run reference = the
submission, a `vendor` source string, a timestamp, an org/requirement attachment,
an overall result, and per-item checks). Reusing the `evidence_runs` +
`evidence_checks` + append-only machinery avoids duplicating it. Isolating the
extra attestation columns in a narrow 1:1 table keeps them from becoming nullable
clutter on every collector row and keeps a single read path for "evidence runs".
Both plans agree on the discriminator + 1:1 extension shape; they differed only
on answer storage (resolved above) and casing (resolved above).

Alternatives rejected: table-per-type (duplicates the checks table and the
immutability triggers); nullable attestation columns directly on `evidence_runs`
(every collector row carries always-null respondent/quiz columns).

### Decision 3: AssessmentResult is computed-on-read, not materialised

No `assessment_results` table. AssessmentResult is a status the read store
computes from `evidence_runs` (the latest run per organisation x requirement) and
`evidence_checks` (that run's checks).

Result vocabulary: both the run-overall `result` and each per-check `result` draw
from a closed set `{Pass, Fail}`. "Failing" means `result == Fail`. Derivation
over the latest run's checks: any `Hard` check with `result == Fail` =>
`HardFailure`; else any `Soft` check with `result == Fail` => `SoftFailure`; else
`Passing`.

Latest run is pinned deterministically per `(organisation_id, requirement_id)` by
`ORDER BY collected_at DESC, received_at DESC, created_at DESC, id DESC`. The `id`
is a ULID (monotonic), giving a total-order tie-break so "latest" is never
ambiguous. The `evidence_runs` index `(organisation_id, requirement_id,
collected_at)` supports this per-pair lookup; the trailing `received_at` /
`created_at` / `id` columns resolve only the (small) tie group within a pair at a
single `collected_at`.

Responsibility split (in-scope enumeration): the store returns a status ONLY for
`(organisation, requirement)` pairs that HAVE at least one evidence run - deriving
`HardFailure` / `SoftFailure` / `Passing` from that pair's latest run. The store
does NOT enumerate the in-scope set and does NOT emit `NoEvidence`. Computing
`NoEvidence` for in-scope pairs that have no store row, and intersecting the result
with the resolved in-scope set, is the WEB caller's responsibility: the canonical
SoA scope resolver (standard-scope cascade, org-hierarchy inheritance,
`Undetermined` handling) lives in `src/Freeboard/Compliance/OrgScope.cs` +
`StatementOfApplicability.cs` (the web project), and the caller already holds the
resolved in-scope set via `OrgScope.InScopeIds`. `Freeboard.Persistence` must NOT
reference that resolver and must NOT re-implement the cascade in SQL (it would
drift). `NoEvidence` stays in the status vocabulary as a real caller-derived
status.

The status names what the evidence shows, not compliance (Codex H3): with no
expected-check catalogue a run can under-report checks, so `Passing` asserts only
"the latest run has no failing hard check", never "the requirement is satisfied".

Rationale: computed-on-read is always correct, needs no recompute trigger, and
adds no table or write path - the lowest-liability option, matching the existing
Statement-of-Applicability projection computed-on-read from a `RepeatableRead`
snapshot. Volume is small (requirements x organisations) and the surface is a
read-only, non-hot page. Both plans agree; a materialised table would need
invalidation on every evidence append and every GitOps scope/requirement change.
If profiling later shows a need, materialisation can be added as a cache behind
the same `IEvidenceStore` read contract without changing callers.

### Decision 4: Append-only enforced at both the store and the database (Codex H1)

Primary enforcement: `IEvidenceWriteStore` exposes only append operations
(`AppendEvidenceAsync`, `AppendAttestationResponseAsync`) plus nothing else. There
is no update or delete method, so no code path can mutate a recorded run.

Backstop (makes immutability real, not merely conventional): migration `011`
creates `BEFORE UPDATE` and `BEFORE DELETE` triggers on `evidence_runs`,
`evidence_checks`, and `attestation_responses` that `SIGNAL SQLSTATE '45000'` with
a clear message, rejecting any stray UPDATE or DELETE against a recorded run even
from a raw SQL client. Triggers are single-statement `SIGNAL` bodies (no
`BEGIN ... END` block), so the migration runner's statement batching is not
affected. Each trigger is created idempotently with a `DROP TRIGGER IF EXISTS`
immediately before a plain `CREATE TRIGGER` (no `CREATE TRIGGER IF NOT EXISTS`),
so a replayed partial migration re-runs cleanly.

INSERTs are unaffected (triggers fire only on UPDATE/DELETE), so appending a run
plus its checks plus an optional attestation extension in one transaction works
normally. `DROP DATABASE` in test teardown is DDL and does not fire row triggers,
so throwaway test databases still drop cleanly.

Appends use plain `INSERT` statements only. A `UNIQUE` violation on
`(vendor, collector_ref)` or `(evidence_id, name)` is caught and mapped to a
failing `WriteResult` with the transaction rolled back; rolling back uncommitted
`INSERT`s does not fire the delete trigger. `INSERT ... ON DUPLICATE KEY UPDATE`,
`REPLACE INTO`, and `INSERT IGNORE` are forbidden: the first two trip the
`BEFORE UPDATE` / `BEFORE DELETE` triggers, and `IGNORE` silently swallows the
collision instead of surfacing it.

DELETE is blocked as well as UPDATE so append-only is real for history, not just
for field edits. Retention/GC is a documented Non-Goal; if introduced later it
would be a deliberate, separately reviewed migration that relaxes the delete
trigger.

### Decision 5: IDs, references, and idempotency key

- Evidence and check ids are runtime-generated ULIDs stored `CHAR(26) ...
  utf8mb4_bin`, generated through the existing `IUlidFactory` seam (matching auth
  and authz-audit ids). `attestation_responses` is keyed 1:1 on its
  `evidence_id`.
- Internal FKs among the new tables are enforced: `evidence_checks.evidence_id`
  and `attestation_responses.evidence_id` both reference `evidence_runs(id)`. The
  delete trigger blocks deletes in practice; the internal FK is a model backstop.
- `UNIQUE (evidence_id, name)` on `evidence_checks`: a run has no two checks with
  the same name. This unique key also covers the `evidence_id` FK lookup via its
  leftmost prefix, so `evidence_checks` needs no separate standalone
  `(evidence_id)` index.
- `UNIQUE (vendor, collector_ref)` on `evidence_runs`: both columns are `NOT NULL`
  and `collector_ref` is a per-run idempotency key (the vendor's stable id for this
  observation), so a re-delivered observation is deduped. See the idempotency
  divergence above.
- Idempotency / identity string keys use `CHARACTER SET utf8mb4 COLLATE
  utf8mb4_bin` (matching the exact-byte id identity in Context): `evidence_runs`
  `vendor` and `collector_ref`, and `evidence_checks.name`. This makes both unique
  constraints case-sensitive exact-byte matches, so `VendorA`/`ref1` does not
  collide with `vendora`/`REF1`, and two checks named `Tls` and `tls` are distinct.
- No FK to `requirement_scopes` (Codex M2): the importer replaces that table
  wholesale on every sync, so an evidence FK into it would break sync. Evidence
  stores `requirement_id` (plus the run's `organisation_id`) directly.
- **External references** - `evidence_runs.organisation_id`,
  `evidence_runs.requirement_id`, and `attestation_responses.user_id` - are scalar
  id columns with **no** strict FK, following `authz_audit_events` (the mediator
  selected this, Option A; see "Resolved decision: external FK strategy" below).
  They are `VARCHAR(190) ... utf8mb4_bin` for org/requirement and `CHAR(26) ...
  utf8mb4_bin` for the user, matching those tables' id column types, and are
  indexed for the read path.

### Decision 6: Domain model / read-model placement

Read-model records (`EvidenceRunRow`, `EvidenceCheckRow`,
`AttestationResponseRow`, `AssessmentResultRow`) live in `Freeboard.Persistence`
(`EvidenceReadModels.cs`), alongside the existing `ComplianceReadModels.cs`. No
new `Freeboard.Core` types. Severity (`Hard`/`Soft`), kind, result, and
assessment status are validated as strings in the persistence write/read layer.
See the Core-enums divergence above and Open Questions.

## Resolved decision: external FK strategy

### External FK strategy for evidence's org / requirement / user references

**Decision: Option A (scalar id columns, no strict FK).** Both plans agree
evidence carries `organisation_id`, `requirement_id`, and `user_id`; they
disagreed on whether these get strict database foreign keys. The mediator
resolved this on the plan-phase review by selecting **Option A**, matching the
`authz_audit_events` precedent and the design's shipped default. The option
analysis below is retained as rationale.

**Which tables/columns are in scope:** `evidence_runs.organisation_id` ->
`organisations(id)`, `evidence_runs.requirement_id` -> `requirements(id)`, and
`attestation_responses.user_id` -> `users(id)`. (There is no FK to
`requirement_scopes` under either option - Decision 5, M2.)

**Option A - scalar id columns, no strict FK (Plan A; selected).**
Follows the `authz_audit_events` precedent (migration 010), which deliberately
uses scalar actor/resource ids with no FK so the trail survives user and
organisation deletes. Evidence is a durable append-only factual record; it
should likewise outlive config churn.

- Importer blast radius: **none**. `MySqlGitOpsImporter` is untouched.
- Decisive point: the importer prunes absent requirements and organisations with
  plain `DELETE FROM requirements WHERE id NOT IN @KeepIds` and a repeated
  absent-organisation delete (see `DeleteAbsentAsync` and
  `DeleteAbsentOrganisationsAsync`). A strict `ON DELETE RESTRICT` FK from
  evidence would make those deletes fail whenever a pruned requirement/org ever
  had evidence. The importer *cannot* pre-prune the evidence to unblock itself,
  because the append-only `BEFORE DELETE` trigger rejects the delete. So strict
  FK + append-only trigger = a GitOps sync that wedges permanently on that
  requirement/org, escapable only by a manual migration.
- Trade-off accepted: no DB-enforced referential integrity for those columns, so
  a run can outlive its requirement/organisation. The read projection joins by id
  and simply omits dangling in-scope pairs (evidence still displays; an
  assessment for a removed requirement falls out of the in-scope projection).
- One-line case: durability and zero importer risk, matching the audited
  precedent; the cost is a possible dangling logical ref that the read path
  already tolerates.

**Option B - strict FK `ON DELETE RESTRICT` + importer preflight (Plan B; not
selected).**
Add FKs on all three external refs and a `MySqlGitOpsImporter` preflight that,
before deleting an org/requirement referenced by evidence, throws a clear
`InvalidOperationException` instead of surfacing a raw FK error.

- Importer blast radius: **new preflight queries** before
  `DeleteAbsentAsync("requirements", ...)` and `DeleteAbsentOrganisationsAsync`,
  each checking `evidence_runs` for a referencing row, plus new failure paths and
  tests. It changes GitOps sync from "prune-and-continue" to "fail the whole sync
  if a to-be-removed requirement/org has evidence".
- Hard limitation: the preflight can only *explain* the block, not clear it. With
  the append-only delete trigger, there is no in-band way to remove the evidence,
  so a config that drops a requirement/org with evidence can never sync until an
  operator runs a manual migration. Strong referential integrity is bought at the
  price of a sync that an ordinary config edit can permanently stall.
- One-line case: real DB-level integrity (no dangling refs, evidence cannot point
  at a vanished requirement), at the cost of importer complexity and a
  config-driven sync deadlock the append-only rule makes unrecoverable in-band.

**Why Option A won:** the append-only trigger and strict `ON DELETE RESTRICT` FKs
are mutually antagonistic - the trigger removes the only tool the importer has to
satisfy the FK - and the repo already set this exact precedent for durable
append-only history in `authz_audit_events`. Option B would have been viable only
paired with an explicit operator runbook (or a narrowly scoped, reviewed
maintenance path) for clearing evidence when a referenced requirement/org is
intentionally removed; the mediator declined that added complexity.

The spec and tasks below encode Option A: scalar external id columns, no strict
FK, indexed for the read path, and no importer preflight.

## Risks / Trade-offs

- Scalar external refs (Option A) mean no DB-enforced integrity for
  organisation/requirement/user on evidence -> Mitigation: matches the audited
  `authz_audit_events` precedent; the read projection joins by id and omits
  dangling pairs; documented explicitly.
- DB triggers reject UPDATE/DELETE, which could surprise an operator doing manual
  data fixes -> Mitigation: intentional (append-only is the requirement); the
  `SIGNAL` message states the run is immutable; retention is a deliberate future
  migration.
- Append-only is a DML-level guarantee: the `BEFORE UPDATE` / `BEFORE DELETE`
  triggers stop UPDATE and DELETE, but DDL (`DROP TABLE`, `TRUNCATE TABLE`) and FK
  cascade deletes do not fire row triggers, so a DB user with DDL/privileged rights
  can still bypass immutability -> Mitigation: run the app with a least-privilege
  runtime DB user that lacks `DROP` / `TRUNCATE` on these tables (documented here;
  no migration grants added).
- Trigger bodies in a batched migration could mis-split -> Mitigation: each
  trigger is a single-statement `SIGNAL` (no `BEGIN ... END`), which the runner's
  batching handles; verified by the migration-applies integration test.
- Creating triggers on a binlog-enabled server can be rejected (MySQL error 1419,
  ER_BINLOG_CREATE_ROUTINE_NEED_SUPER) unless the migration DB user has
  `log_bin_trust_function_creators=1` set on the server or a privilege sufficient
  to create triggers -> Mitigation: documented as a deploy precondition for
  operators in proposal.md, the `011_evidence.sql` migration header, docs/gitops.md,
  and README.md.
- Computed-on-read AssessmentResult could get slow at large scale -> Mitigation:
  small expected volume; the read contract allows a later materialised cache
  without changing callers.

## Migration Plan

1. Add `011_evidence.sql`; `freeboard system migrate` applies it forward-only.
   The runner records the version only after success, so a partial failure
   re-runs cleanly (hence `CREATE TABLE IF NOT EXISTS` on tables, and
   `DROP TRIGGER IF EXISTS` before each plain `CREATE TRIGGER`).
2. No data backfill: the tables start empty; no existing rows are touched.
3. Rollback: forward-only per repo policy. Pre-1.0 there is no down migration; a
   correcting migration would be authored if needed. The change is additive
   (new tables only), so applying it cannot break existing reads or writes.

## Open Questions

- **External FK strategy**: resolved. The mediator selected Option A (scalar id
  columns, no strict FK, indexed) - see "Resolved decision: external FK strategy".
- Read-model / status types placement: resolved. They live in
  `Freeboard.Persistence` (`EvidenceReadModels.cs`, matching
  `ComplianceReadModels.cs`), string-validated; no new `Freeboard.Core` types.
- Overall run `result` and per-check `result` vocabulary is currently the closed
  set `{Pass, Fail}`, validated as strings by the write store (Decision 6). It MAY
  be extended (e.g. `Inconclusive`, `NotApplicable`) behind the same read contract
  when a collector or surface needs it; the `Fail`-means-failing derivation rule
  would then continue to apply to `Fail` only.
