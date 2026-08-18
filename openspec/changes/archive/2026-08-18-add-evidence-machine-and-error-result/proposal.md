## Why

Evidence today records one run per `(organisation, requirement, collector)` with a flat
list of checks, and the run-overall `result` is closed to `Pass` or `Fail`. Integration
collection produces one result per machine, and it fails in ways that are not a policy
failure. Neither fact fits the current row.

Two gaps follow from that. A per-machine collector has nowhere to record which machine a
run describes, so a fleet of 500 hosts collapses into one verdict that hides which host
failed. A collection attempt that could not reach the provider has to be recorded as
`Pass`, as `Fail`, or not at all. `Pass` is a false green, `Fail` claims a policy breach
that was never observed, and recording nothing lets the last good run stand until it goes
`Stale` several cadence windows later.

The in-process integration runner cannot be written until the run can carry a machine and
can say "collection failed". This change moves the append-only evidence core and its single
read-side rollup so that runner has a place to write.

This change is MIT. It extends `Freeboard.Persistence` (schema, read models, both evidence
stores), two read surfaces in the web app, and the web app's collector scheduler. None of it
is a paid, enterprise-gated feature, so no part of it belongs in
`src/Freeboard.Enterprise`.

## Vocabulary: the older terms and their current names

The per-machine evidence idea predates the unification of the object model, so it is stated
in terms that no longer name anything in the schema. Those terms map onto the current model
as follows, and the rest of this proposal uses only the current names.

| Older term | Current name |
| --- | --- |
| `Machine` asset | An `assets` row with `type = 'Machine'` and `source = 'discovered'`. Migration `019` merged `organisations`, `vendors`, and `asset` into one `assets` table. |
| `Organisation` | An `assets` row with `type = 'Company'` or `type = 'Department'`. The column name `evidence_runs.organisation_id` is unchanged. |
| `EvidenceCollector` | A `collectors` row. Migration `021` merged `evidence_collectors` and `attestation_templates` into one `collectors` table. |
| "connection" | `integration_connections.id`, reached from a collector through `collectors.connection_id`. |
| "control" | `collectors.control_id`. A collector names exactly one control. |
| "cycle" | The collector scheduler's `collector_scheduler_state.current_run_id`. This token already exists and is already dispatched to `IScheduledCollectorRunner`. |

The older statement of the idempotency key is `(connection, control, machine, cycle)`. A
collector already names exactly one control and, when its type is `integration`, exactly
one connection, so a collector id subsumes the first two terms. One dispatch of one
collector can still produce many runs, because a control maps to many requirements and a
collector serves many organisations. The key therefore has to name the organisation and
the requirement as well. The concrete key this change adds is
`(cycle_id, organisation_id, requirement_id, asset_key)`, and `cycle_id` names the
collector because the scheduler mints that token on one collector's state row. Design
decision D3 records the byte arithmetic that also rules out a wider key: the four parts
already reach 2384 of the 3072 bytes InnoDB allows.

## What Changes

- `evidence_runs` gains a nullable scalar `asset_id`. It names the machine the run
  describes. It is a second dimension under the run's organisation, not a replacement for
  it. The run's subject stays the organisation, so the evidence ingest rule that rejects a
  `Machine` as `organisation_id` is unchanged.
- `evidence_runs` gains a nullable `cycle_id` recording the collection cycle the run
  belongs to, plus a new unique key
  `(cycle_id, organisation_id, requirement_id, asset_key)`. `asset_key` is a generated
  column holding `COALESCE(asset_id, _utf8mb4'')`, because MySQL treats each `NULL` as distinct
  and a nullable `asset_id` alone would not dedup an organisation-level run.
- The run-overall `result` set opens from `{Pass, Fail}` to `{Pass, Fail, Error}`. A
  consumer that treats the run result as a two-value set has to widen. The per-check
  `result` set stays `{Pass, Fail}`.
- `evidence_runs` gains an `error_detail` column. An `Error` run SHALL carry a detail and
  every other run SHALL leave it null. An `Error` run often carries no checks, so without
  the detail the append records that something failed but not what.
- `evidence_runs.vendor` and `evidence_runs.collector_ref` relax to nullable. Two check
  constraints hold the line the relaxation gives up: the pair is both-or-neither, and every
  run carries exactly one of that pair or the pair `(collector_id, cycle_id)`. The
  `EvidenceRunRow` and `NewEvidenceRun` read models expose `Vendor` and `CollectorRef` as
  `string?` in step with the columns. An in-process per-machine run has no vendor-supplied
  observation id and must not fabricate one.
- The existing `UNIQUE (vendor, collector_ref)` key stays, unconditional and unchanged. It
  is the ratified idempotency key of the HTTP ingest contract. The identity constraint is
  exclusive, so a run carries the legacy pair or a cycle and never both, and the two keys
  cannot contend.
- The derived per-collector status set gains `Errored`, between `HardFailure` and `Stale`
  in precedence. The full precedence becomes
  `HardFailure > Errored > Stale > SoftFailure > Passing`.
- The status derivation gains a cycle-wide assessed set. When a collector's latest run
  carries a `cycle_id`, its status is the worst status across every run of that cycle, so
  one errored or failing machine is never hidden by a passing sibling. When the latest run
  carries no `cycle_id`, the derivation is unchanged and reads that one run.
- The rollup picks that assessed set in SQL. A window function pins each group's latest run,
  the database returns only the pinned cycle's runs, and each run's failing-check flags are
  aggregated server-side. The read no longer ships every historical run to the application
  to fold in memory, and the separate check query folds into the one statement.
- The scheduler ends a collection cycle on every path where its runner RETURNED NORMALLY.
  Today an early return on a cancelled token skips the completion even when the runner
  finished, so a token can outlive the cycle it names. The cycle idempotency key depends on
  the clear, so this change closes that path. The early return stays for the case it is
  actually needed for: a runner that honored its token and threw. That dispatch records no
  outcome at all, because charging a cancellation to the collector's failure budget would
  march it toward the terminal `dead` status.
- A run appended under the cycle key is final for that cycle: append-only storage plus a
  retry that reuses the same cycle token mean a later answer for the same machine cannot
  replace an earlier one. A producer therefore appends a run only for an outcome it will not
  retry under that cycle id.
- The Statement of Applicability page and the control-detail anatomy render `Errored`
  distinctly from `Stale`, `Unknown`, and the two failure states.
- The UX status rule gains a bounded carve-out. The page's collector row writes its own
  words for a collection state, and three of them already sit outside the product status
  vocabulary. This change adds a fourth, so it amends the rule to permit a closed set of six
  evidence-status labels on that one surface rather than leaving a ratified rule that the
  page does not follow. The carve-out governs the status LABEL. A supplementary note that
  states the collection state in plain words stays permitted on any surface, which is what
  the control anatomy already renders beside its seal.
- Every enum-like comparison this change moves into SQL collates its literal
  `utf8mb4_0900_bin`. The compared columns inherit the server's case-insensitive default
  collation, and a bare literal would make the new check constraints and the new rollup
  predicates disagree with the ordinal comparisons they replace. The collation is the NO PAD
  binary one, so a trailing space is significant, as it is in the ordinal comparison.
- One forward-only migration, `024`. It adds columns, one generated column, one unique
  key, and four check constraints. It backfills nothing and it neither drops nor
  re-creates the six append-only triggers.

No change to the `freeboard.evidence.v1` HTTP contract, its JSON Schema, or its documented
idempotency key.

## Capabilities

### New Capabilities

None. Every behavior added here belongs to an existing capability.

### Modified Capabilities

- `evidence-persistence`: the run row gains the machine, cycle, and error-detail columns,
  the run `result` set opens to three values, a second idempotency key is added, and the
  derived status gains `Errored` and a cycle-wide assessed set.
- `evidence-ingest`: the endpoint's derived verdict stays `Pass` or `Fail` and SHALL never
  produce `Error`. The rationale for rejecting a `Machine` as `organisation_id` is
  restated, because the run now carries a machine reference in its own column.
- `integration-connection`: the requirement that an unresolvable token is not masked as an
  evidence run currently reasons from a `result` set closed to `{Pass, Fail}`. That
  premise changes, so the requirement is restated against the three-value set.
- `statement-of-applicability`: the page renders `Errored` distinctly from `Stale` and
  `Unknown`.
- `collector-scheduler`: the run token's full lifecycle is written down, and the scheduler
  service is fixed to honor it. The capability already states that a claimed collector is
  dispatched with its stable `current_run_id` "so a future real runner can make its work
  idempotent on that id", and that a failed run keeps the token for its retry. It does not
  state what ends a cycle. The new idempotency key depends on that ending, so the delta
  states it, defines "returned normally", and separates a runner that finished from a runner
  that was cancelled. The ratified failure requirement is restated in the same delta, because
  it says a dispatch that fails records a failure while this change ratifies that a dispatch
  the service cancelled records nothing. The restatement scopes "fails" to a failure the
  runner reported.
- `scheduler-lease`: the ratified scenario for a lost lease says the worker cancels the
  in-flight dispatch "instead of completing it". The worker now cancels and then attempts a
  fenced completion, which the fence declines. The delta restates the scenario so the
  attempt is covered: the write matches no row, no run outcome is recorded, and the new
  holder's state is untouched.
- `web-ux-conventions`: rule S1 gains a bounded carve-out for the evidence-status labels on
  the Statement of Applicability collector row. The permitted set is closed at six labels,
  the carve-out reaches no other surface, and S2 and S3 keep applying to the row.

No `asset-model` delta. `evidence_runs.asset_id` is a scalar reference with no foreign key,
matching the existing `organisation_id` and `collector_id` columns, so the asset capability
gains no obligation.

No `compliance-web-read` delta. The derived status appears on pages, not on any JSON read
endpoint, and this change adds no field to any endpoint.

## Impact

Code:

- `src/Freeboard.Persistence/Migrations/024_evidence_machine_and_error.sql` - new.
- `src/Freeboard.Persistence/EvidenceReadModels.cs` - three members on `EvidenceRunRow`,
  two members relaxed to nullable, and the status vocabulary doc.
- `src/Freeboard.Persistence/IEvidenceWriteStore.cs` - three optional members on
  `NewEvidenceRun` and the idempotency contract.
- `src/Freeboard.Persistence/MySqlEvidenceWriteStore.cs` - the insert column list,
  validation of the widened result set and the identities (run against the values the store
  will write, not the caller's), the conflict message, and a mapping of MySQL error 3819 so a
  check-constraint violation returns a failing write result instead of escaping the store.
- `src/Freeboard.Persistence/IEvidenceStore.cs` - the derived status contract.
- `src/Freeboard.Persistence/MySqlEvidenceStore.cs` - the status query becomes a windowed
  statement that pins the assessed set and aggregates the check outcomes, plus the selected
  columns and `DeriveStatus`.
- `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml` - one badge case.
- `src/Freeboard/Pages/Compliance/ControlDetailProjection.cs` - one status map entry and
  one note entry.
- `src/Freeboard/Scheduler/CollectorSchedulerService.cs` - the dispatch attempts its fenced
  success completion on every path where the runner returned normally, the early return on a
  cancelled token moves below that arm so a cancelled dispatch never records a failure, and
  both completion writes run under a fresh, short-lived `CancellationTokenSource` instead of
  the host stopping token.
- `src/Freeboard/Scheduler/IScheduledCollectorRunner.cs` - the runner contract's XML doc
  states that a runner which has not finished must throw rather than return, because the
  service reads a normal return as a completed cycle.
- `src/Freeboard/stories/UxRules.mdx` - the visual reference restates rule S1 word for word,
  so it gains the same carve-out the rule does.
- `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs` - one comment that states
  `evidence_runs.vendor` is `NOT NULL`, which the migration makes false. The endpoint's
  behavior is unchanged: it still rejects a collector with no vendor.

The two page files carry two DIFFERENT status-to-badge maps for the same control, and they
already disagree about `Stale`. `Errored` has to be added to both. Added to one only, it
falls through the other's default and renders as "not collected".

Tests:

- `tests/Freeboard.Persistence.Tests/EvidenceIntegrationTests.cs` - migration shape, cycle
  idempotency, `Error` runs, the per-machine rollup, and two raw-SQL cases that prove the
  binary collation. Its
  `Migration015AddsNullableColumnsWithoutBackfill` test rebuilds the pre-015 table shape by
  dropping `collector_id`, which a check constraint naming that column now blocks, so the
  test needs a rewrite.
- `tests/Freeboard.Web.Tests/FakeEvidenceStores.cs` - the fake carries a copy of the
  precedence rule and a copy of the collector-identity fallback. Both need the new shape.
- `tests/Freeboard.Web.Tests/StatementOfApplicabilityPageTests.cs` and
  `ControlDetailPageTests.cs` - the new rendered state.
- `tests/Freeboard.Web.Tests/EvidenceIngestEndpointTests.cs` - the ingest rules the widened
  row makes assertable: an ingested run carries no machine and no cycle, always carries a
  vendor and a collector reference, never records `Error`, and replays on the legacy key.
- `tests/Freeboard.Web.Tests/CollectorSchedulerServiceTests.cs` and
  `FakeCollectorSchedulerStore.cs` - the completion paths, a runner that rethrows
  `OperationCanceledException` under shutdown and records no failure, and a fake that models
  a lost lease as another holder owning the row.

Dependencies: none added.

Operational: migration `024` is forward-only and additive. It rewrites no row and drops no
trigger. It is not a metadata-only change: adding a unique key over a generated column and
modifying two columns an existing unique key covers is served by `ALGORITHM=COPY`. MySQL
8.4.10 refuses the statement under `ALGORITHM=INPLACE, LOCK=NONE` with error 1845 and accepts
it with no clause. A copy rebuilds the table and BLOCKS concurrent writes to `evidence_runs`
for the whole rebuild, HTTP evidence ingest included. The algorithm the server picks is
confirmed against a real MySQL 8.4 before the migration is committed, and the header states
the observed algorithm and the write impact. The new unique key also costs an index write on
every later insert, including every HTTP ingest, whether or not the run carries a cycle. Every existing row reads
back a null `asset_id`, a null `cycle_id`, and a null `error_detail`, and keeps the status it
had.

## Non-goals

- Any producer of a machine-scoped or `Error` run. Nothing in the app writes one after this
  change. The in-process integration runner is the first producer, and this change
  exists to unblock it. The behavior is proven by persistence integration tests that append
  through the store rather than by a live collection path.
- Any change to the `freeboard.evidence.v1` wire contract. Adding an optional machine field
  to the HTTP payload is a separate change against a frozen, externally consumed contract.
- A per-machine read surface. The rollup stays keyed on `(organisation, requirement,
  collector)` so no page changes shape. A fleet view that lists the failing hosts of a
  cycle is separate work.
- Making the scheduler write an `Error` evidence run when a dispatch fails. The scheduler
  owns its own per-collector health state. Whether a dispatch failure also appends evidence
  is a decision for the runner that performs integration execution, and this change bounds
  that decision: a failure the scheduler will retry under the same cycle id must not be
  appended, because the retry's better answer could not replace it. The scheduler change in
  this proposal is limited to ending a cycle's token when the dispatch succeeded.
- Reconciling the two status-to-badge maps. They already disagree about `Stale`, and folding
  them into one mapping would widen this change into page rendering. `Errored` is added to
  both maps and the pre-existing `Stale` disagreement stands.
- Restating the Statement of Applicability row in the product status vocabulary. The UX
  delta ratifies the six labels the row uses and closes the set. It does not rewrite the
  five existing labels, which is a page-rendering change with its own tests.
- Changing the collation of `evidence_runs.result`, `evidence_runs.kind`,
  `evidence_checks.severity`, or `evidence_checks.result`. The comparisons this change writes
  declare a NO PAD binary collation on their literals instead, so no second table is rebuilt.
- A backfill. No existing run has a machine or a cycle, and inventing one would fabricate
  history on an append-only table.
- A C# enum for the status vocabulary. The vocabulary stays stringly typed end to end, as
  it is today. Typing it is a worthwhile cleanup with a blast radius of its own.
- An index on `asset_id`. Migration `015` refused an index on the append-hot
  `evidence_runs` table for the same reason, and the existing
  `(organisation_id, requirement_id, collected_at)` index already serves every read this
  change touches.
