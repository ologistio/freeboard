## Context

The evidence core is the most mature append-only surface in the repo, and every read-side
rollup runs through one method. The current state, verified against the code:

- `evidence_runs` (migration `011`, extended by `015`) holds `id`, `kind`,
  `organisation_id`, `requirement_id`, `collector_ref`, `vendor`, `result`, `collected_at`,
  `received_at`, `raw_payload`, `created_at`, `collector_id`, and `frequency`.
  `organisation_id`, `requirement_id`, and `collector_id` are scalar columns with no
  foreign key, so a run survives deletion of its organisation, requirement, or collector.
- `UNIQUE (vendor, collector_ref)` is the only idempotency key. Both columns are
  `NOT NULL VARCHAR(190) utf8mb4_bin`. The HTTP ingest endpoint composes `collector_ref` as
  `collector_id:run_id` and rejects `:` in either part, so the composition is unambiguous. A
  collision is an accepted replay and answers `200`.
- Six `BEFORE UPDATE` / `BEFORE DELETE` triggers `SIGNAL` on `evidence_runs`,
  `evidence_checks`, and `attestation_responses`. They reference no column, so an
  `ALTER TABLE ... ADD COLUMN` neither drops nor invalidates them.
- `MySqlEvidenceStore.GetCollectorEvidenceStatusesAsync` is the single rollup. Under one
  `RepeatableRead` snapshot it reads every `kind = 'Collector'` run for the supplied
  organisations, groups in memory by `(organisation, requirement, collector)`, pins the
  latest run by `collected_at, received_at, created_at, id` descending, reads that run's
  checks, and returns `HardFailure`, `Stale`, `SoftFailure`, or `Passing` in that
  precedence. It never emits `Unknown`.
- Exactly two callers consume the status:
  `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml` (an inline `switch` to a
  badge) and `src/Freeboard/Pages/Compliance/ControlDetailProjection.cs` (a map to
  `StatusKind` plus a note). Both default an absent status to `Unknown`. Neither the CLI
  nor `Freeboard.Enterprise` nor `Freeboard.Agent` reads either vocabulary, and no
  compliance percentage or score is computed anywhere in the repo.
- `StatusKind` is a closed product-wide vocabulary owned by the `web-ux-conventions` rules.
  A page cannot invent a status, so a new evidence status has to map onto an existing
  member.
- `collector_scheduler_state` has `PRIMARY KEY (collector_id)`, so there is exactly one
  scheduler row per collector. Its `current_run_id` is a ULID minted by the claim
  (`current_run_id = COALESCE(current_run_id, @NewRunId)`), retained by
  `CompleteFailureAsync` so a retry reuses it, set to `NULL` by `CompleteSuccessAsync`, and
  set to `NULL` again when a config change revives a `dead` or `error` row. Both completion
  writes are fenced (`WHERE collector_id = @Id AND lease_token = @Token`), so a worker that
  lost its lease changes nothing.
  `IScheduledCollectorRunner.RunAsync(collector, runId, ct)` already receives that token.
  The default runner is a no-op that appends nothing.
- `CollectorSchedulerService.DispatchAsync` does not always reach a completion write. When
  the heartbeat reports a lost lease, or when the host stops, the linked token is cancelled
  and the method returns before any completion. It also passes the host stopping token to
  the completion write, so a shutdown between the runner returning and the write cancels
  the write.

Constraints: MIT only, so nothing may land in or reference `Freeboard.Enterprise`. The
reference graph is Core -> nothing, Persistence -> Core, CLI -> Core plus Persistence, web
-> Core plus Enterprise plus Persistence. `Freeboard.Agent` and `Freeboard.CLI` stay
EE-free and cross-platform, and neither is touched by this change. No new package
dependency. Migrations are forward-only files discovered by filename ordinal, so a new
`024_*.sql` is picked up with no registration.

## Goals / Non-Goals

**Goals:**

- One nullable machine reference on an evidence run, as a dimension under the run's
  organisation rather than a replacement for it.
- A cycle-scoped idempotency key, so a re-delivered or retried collection cycle appends
  each run once.
- A third run-overall result, `Error`, that is never counted as passing and is
  distinguishable from a policy failure and from staleness.
- A derived status that reports the worst outcome across a cycle's machines, with no
  change to the shape of the row the two pages already consume, and whose cost is bounded
  by the newest cycle rather than by the whole run history.
- A scheduler run token whose lifecycle actually matches the one the cycle key depends on.
- Every existing evidence path (attestations, script collectors, agent collectors, HTTP
  ingest) works unchanged after the migration.

**Non-Goals:**

- Writing a machine-scoped or `Error` run from any live code path. See the proposal's
  Non-goals.
- Changing the `freeboard.evidence.v1` wire contract or its JSON Schema.
- A per-machine read surface, a per-machine status row, or a fleet page.
- Replacing `(vendor, collector_ref)`. It stays as the HTTP contract's ratified key.
- Typing the status vocabulary as a C# enum.
- Any scheduler change beyond ending a cycle whose runner returned normally, and keeping a
  cancelled dispatch off the failure path. Leasing, backoff, the dead transition, the
  fingerprint, and the runner seam are untouched.

## Decisions

### D1. The machine is a nullable dimension, not a new subject

`evidence_runs` gains `asset_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
NULL`. It is a scalar reference with NO foreign key, matching `organisation_id`,
`requirement_id`, and `collector_id`. A retired or deleted machine must never block an
append and must never cascade an append-only row away.

`organisation_id` keeps its meaning. A machine-scoped run still names the organisation the
machine belongs to. This preserves the ratified `evidence-ingest` rule that a payload
naming a `Machine` as `organisation_id` is a `422`: the machine gets its own column rather
than taking the organisation's.

`asset_id` is null for every run that describes the organisation as a whole. That is every
attestation, every existing run, and every organisation-level integration check.

Alternatives considered:

- **A separate `evidence_run_assets` join table.** Rejected. A run describes exactly one
  machine or none, so a join table models a cardinality that does not exist and adds a
  second append-only table with its own triggers.
- **Replacing `organisation_id` with a generic `subject_id`.** Rejected. It would break the
  ingest admission gate, the batch read signature, both pages, and the authorization
  narrowing, for no gain: the organisation is still needed to group the rollup.

### D2. "Cycle" is the scheduler's existing run token, promoted onto the run

This change introduces no new concept. `collector_scheduler_state.current_run_id` is
already a ULID minted per collector, already preserved across a retry, and already
dispatched to the runner. `evidence_runs` gains
`cycle_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL` to record it.

Three properties of that token carry the idempotency key:

1. **It is per collector.** `collector_scheduler_state` is keyed by `collector_id`, and the
   claim mints the ULID onto that one row. Two collectors never read one token from one row.
2. **It is stable across a retry.** `CompleteFailureAsync` writes `current_run_id =
   current_run_id`, so the retried dispatch re-appends under the same key and collides
   instead of duplicating.
3. **A successful cycle discards it.** `CompleteSuccessAsync` sets `current_run_id = NULL`,
   so the next claim mints a fresh ULID and the next cycle's runs do not collide with the
   last cycle's.

Properties 1 and 2 hold as written. Property 3 does not hold on every path today, and it is
the property the key cannot live without.

`CompleteSuccessAsync` clears the token only when its fenced `WHERE collector_id = @Id AND
lease_token = @Token` still matches, and `CollectorSchedulerService` reaches that call on one
path out of three. A lost lease or a stopping host cancels the dispatch's linked token and
the method returns with no completion. A shutdown that lands between the runner returning and
the completion write cancels the write, because the write takes the host stopping token. On
those paths the token outlives the cycle it names. The next claim then re-`COALESCE`s the
same `current_run_id`, and every append of the re-dispatched cycle collides with the runs the
first dispatch already wrote.

This change therefore fixes the paths as well as writing the lifecycle down:

- The dispatch attempts the fenced `CompleteSuccessAsync` whenever the runner RETURNED
  NORMALLY, including under a cancelled linked token. The fence is what makes this safe. If
  another worker holds the lease, the write matches no row and changes nothing.
- The early return on a cancelled linked token STAYS, and it moves below the success arm. It
  now guards one path only: a dispatch whose runner threw while the token was cancelled.
- The completion writes run under a fresh `CancellationTokenSource` that is not linked to the
  host stopping token and that carries a short timeout, so a dispatch that finished still
  ends its cycle without holding host teardown open.
- A lost lease is left alone on purpose. The row belongs to the new holder, which
  re-dispatches the same cycle id, which is property 2 rather than a leak. A producer
  therefore treats a re-delivered append under its own cycle id as an accepted replay, not
  as a failure. A producer that failed the dispatch on the collision instead would keep the
  token alive through every retry until the collector went `dead`.

"Returned normally" is the whole of it, and the fix is narrower than "the token is cleared
whenever nothing went wrong". Three cases have to be told apart:

1. **The runner returns normally under an uncancelled token.** The completion already ran
   today. Nothing changes.
2. **The runner returns normally under a CANCELLED token.** This is the only case the fix
   converts. The completion now runs and records `status = 'ok'`, a null `current_run_id`,
   and a `next_due_at` advanced by a full interval. That is the correct reading: a runner
   that returns normally under a cancelled token is asserting that it finished its work, and
   the documented runner contract says a runner that has NOT finished throws instead. A
   runner that returns early and silently under cancellation is a runner bug, and this change
   records its claim rather than trying to second-guess it.
3. **The runner throws because the cancellation reached it.** This is what a token-honoring
   runner does, so it is the common case on both a lost lease and a host shutdown, and the
   fix deliberately does NOT reach it. `OperationCanceledException` is not fatal by
   `IsFatal`, so the throw lands in the failure variable and the dispatch is on the FAILURE
   path. Without the retained early return it would reach `CompleteFailureAsync`, which
   increments `failure_count`, sets `status = 'error'`, and applies a backoff, and which
   moves the collector to the terminal `dead` status once the failure count reaches
   `MaxAttempts`. A dead collector is never claimed again until a config change revives it.
   Cancelling our own work must not cost a collector its failure budget, so the dispatch
   records nothing and the token stays in place for the retry.

The token retained in case 3 is the designed outcome, not a residual leak. Property 2 says a
retry reuses the token, and a cancelled dispatch is exactly a run that will be retried.

The `collector-scheduler` delta carries both the lifecycle and the behavior change (see D12).

`cycle_id` is null for every producer that has no scheduler dispatch behind it, which today
is all of them.

### D3. The new idempotency key is `(cycle_id, organisation_id, requirement_id, asset_key)`

`asset_key` is a generated column, `VARCHAR(190) ... GENERATED ALWAYS AS (COALESCE(asset_id, _utf8mb4
_utf8mb4''))  VIRTUAL`, carried in the key instead of `asset_id` itself. MySQL treats each `NULL` in
a unique index as distinct, so an organisation-level run (`asset_id IS NULL`) would not
dedup at all. `VIRTUAL` stores nothing in the row and only materializes in the index.

`organisation_id` and `requirement_id` are in the key because one dispatch of one collector
produces many runs: a control maps to many requirements, and a collector serves many
organisations.

`collector_id` is deliberately NOT in the key, for two reasons.

It is redundant. Per D2 property 1, a `cycle_id` is minted onto exactly one collector's
scheduler row, so the token already names the collector. Uniqueness across collectors rests
on ULID randomness, which is the same assumption every id in this repo already makes,
including `evidence_runs.id` as a primary key. The failure mode of a hypothetical collision
is also benign: two collectors sharing a token would make one valid append return a
conflict, not misattribute a run, because the rollup groups on `collector_id` and the
`ck_evidence_runs_cycle_identity` constraint requires `collector_id` on every cycle-keyed
run.

It also does not fit. InnoDB caps an index key at 3072 bytes with the default `DYNAMIC` row
format, and `utf8mb4` reserves 4 bytes per character:

| Key part | Column type | Bytes |
| --- | --- | --- |
| `cycle_id` | `CHAR(26)` | 104 |
| `organisation_id` | `VARCHAR(190)` | 760 |
| `requirement_id` | `VARCHAR(190)` | 760 |
| `asset_key` | `VARCHAR(190)` | 760 |
| Total | | 2384 |

Adding `collector_id VARCHAR(190)` would take the key to 3144 bytes and the `ALTER TABLE`
would fail with error 1071.

Alternatives considered:

- **Keying on the connection and the control instead of the cycle's collector:
  `(connection_id, control_id, requirement_id, asset_key, cycle_id)`.** Rejected on two
  independent grounds. It omits `organisation_id`, so two organisations that a single
  collector serves would falsely collide on their organisation-level runs in one cycle. It
  is also 3144 bytes by the arithmetic above (`connection_id` and `control_id` are both
  `VARCHAR(190) utf8mb4`), so InnoDB would refuse it. Both terms it adds are already implied:
  a collector names exactly one control, and an integration collector names exactly one
  connection.
- **Shortening key parts with prefix lengths, for example `organisation_id(64)`.** Rejected.
  A prefix index makes uniqueness a heuristic: two ids sharing the first 64 characters would
  collide and one valid run would be lost. An idempotency key must be exact.
- **A functional key part, `(COALESCE(asset_id, ''))`, with no visible column.** Rejected.
  MySQL implements it as a hidden generated column with a server-chosen name, which the
  repo's `information_schema` index assertions cannot read.
- **`asset_id NOT NULL DEFAULT ''`.** Rejected. It leaks an empty-string sentinel into the
  read model and into every query, and it makes "no machine" indistinguishable from a
  producer bug.
- **A single hashed key column.** Rejected. It hides which columns form the key and makes a
  duplicate impossible to diagnose from the row.
- **Replacing `(vendor, collector_ref)` outright.** Rejected. That key is named in the
  ratified `evidence-ingest` contract and in the public `docs/evidence-ingest.md`. The two
  keys cannot contend, because `ck_evidence_runs_cycle_identity` admits exactly one of the
  two identities per row (D5), so a cycle-keyed run leaves `vendor` and `collector_ref` null
  and a ref-keyed run leaves `cycle_id` null.

### D4. `vendor` and `collector_ref` relax to nullable, and stay both-or-neither

An in-process per-machine run has no vendor-supplied observation id. `collectors.vendor_id`
and `integration_connections.vendor_id` are both nullable, so it may have no vendor either.
Forcing it to fabricate both, only to satisfy a key it does not use, would put invented
values into an append-only table.

Both columns become nullable and both read models expose them as `string?`. The blast
radius is small and fully enumerated: `EvidenceRunRow`, `NewEvidenceRun`, the store's private
`RunScalar` and `StatusScalar` (Dapper assigns null into a non-nullable `string` member
without complaint, so both records must declare the members `string?` or they lie about their
own rows), the collector-identity fallback (which D8 moves into SQL, where the fallback must
tolerate a null `collector_ref`), and the test fakes. No page reads either member. The
Statement of Applicability page's `check.Vendor` is the collector's vendor, not the run's.

Relaxing a column that an existing unique key depends on is the sharpest hazard in this
change, because MySQL does not dedup on a `NULL` key part. A run carrying `vendor` with a
null `collector_ref`, or the reverse, would be a half-identified run that the legacy key
cannot catch. Two guards close that:

- `ck_evidence_runs_ref_pair` requires the pair to be both null or both non-null, so a
  half-null legacy identity cannot be stored at all.
- `ck_evidence_runs_cycle_identity` requires exactly one of the two identities, so every run
  is covered by exactly one of the two keys.

The HTTP ingest path is unchanged by the relaxation. The endpoint rejects a collector with
no vendor as a `422` before it reaches the store, and it always sets both columns, so every
ingested run still dedups on `(vendor, collector_ref)` exactly as before. The store's
`Validate` keeps rejecting a half-filled pair before any SQL runs, and the constraint is the
database-level backstop for a writer that bypasses the store.

Alternative considered:

- **Keeping `vendor` and `collector_ref` `NOT NULL` and adding generated legacy-key columns,
  so the legacy key applies only to rows with no cycle.** Rejected. It reaches the same
  outcome with strictly more machinery: two more generated columns, a dropped and recreated
  unique index, and a key whose applicability is conditional and therefore harder to reason
  about. Nullable columns plus MySQL's existing NULL-is-distinct behavior give the same
  non-contention with no new index and no rewrite of the ratified key, and the two check
  constraints restore the strictness the relaxation gives up.

### D5. Four check constraints replace what nullability gave up

Added in the same `ALTER TABLE`:

- `ck_evidence_runs_result CHECK (result IN (_utf8mb4'Pass' COLLATE utf8mb4_0900_bin,
  _utf8mb4'Fail' COLLATE utf8mb4_0900_bin, _utf8mb4'Error' COLLATE utf8mb4_0900_bin))`. The
  result set was
  previously enforced only in C#. A third value and a second producer make a database-level
  closed set worth its cost.
- `ck_evidence_runs_error_detail CHECK ((result = _utf8mb4'Error' COLLATE utf8mb4_0900_bin
  AND error_detail IS NOT NULL AND TRIM(error_detail) <> _utf8mb4'') OR (result <>
  _utf8mb4'Error' COLLATE utf8mb4_0900_bin AND error_detail IS NULL))`. An errored run
  states why, and no other run carries a detail. The rule runs both ways so the column means
  one thing (see D6). The test is `TRIM(error_detail) <> _utf8mb4''` rather than
  `error_detail <> _utf8mb4''` because the spec and the store both require a NON-BLANK detail. A constraint that rejected
  only the empty string would let a single space through the backstop while the store
  rejected it, so the two layers would disagree about the same value. Exact parity is not
  claimed: MySQL `TRIM` strips spaces, while `string.IsNullOrWhiteSpace` treats every Unicode
  whitespace character as blank, so a tab-only detail would still pass the constraint. The
  store is the primary rule and normalizes such a detail to null before the insert. The
  constraint is the backstop for a writer that bypasses the store, and it catches the case
  that writer is most likely to produce.
- `ck_evidence_runs_ref_pair CHECK ((vendor IS NULL AND collector_ref IS NULL) OR (vendor IS
  NOT NULL AND collector_ref IS NOT NULL))`. The legacy identity is both-or-neither, so no
  row can be half-identified and slip past the legacy key.
- `ck_evidence_runs_cycle_identity CHECK ((vendor IS NOT NULL AND collector_ref IS NOT NULL
  AND cycle_id IS NULL) OR (collector_id IS NOT NULL AND TRIM(collector_id) <> _utf8mb4''
  AND cycle_id IS NOT NULL AND TRIM(cycle_id) <> _utf8mb4'' AND vendor IS NULL AND
  collector_ref IS NULL))`. Every run carries EXACTLY ONE of the two identities, so
  every run is dedupped by exactly one key.

The cycle arm tests for a non-blank pair rather than a non-null one, because non-null is not
the same as identified here. The read side derives a collector identity only from a NON-EMPTY
`collector_id`, so a row carrying `collector_id = ''` beside a `cycle_id` would satisfy a
null-only check, store, and then contribute to no collector's status at all. The store applies
the same rule over the values it will write, and normalizes a blank `collector_id` or
`cycle_id` to null first, so the two layers read one row the same way.

The identity constraint is exclusive rather than at-least-one on purpose. An at-least-one
form is satisfied by any legacy row, so it would admit a row carrying the legacy pair AND a
cycle. That row would land in both unique keys, and the claim the two keys never contend
would stop being true. The exclusive form makes the claim an enforced property rather than a
convention.

The exclusive form forecloses two future shapes rather than one. The first is a run that
carries a producer-supplied observation id AND a collection cycle. An ingested run can
therefore never carry a cycle, so the HTTP contract cannot later gain a cycle field without a
migration that widens this constraint.

The second follows from the same clause and is easier to miss: the cycle arm requires
`vendor IS NULL`, so a cycle-keyed run can never name a vendor. An in-process collection that
ran against a NAMED provider therefore cannot record which provider it was, even though
`collectors.vendor_id` may hold exactly that. Nothing reads a run's `vendor` today except the
legacy idempotency key, so the loss costs no reader now. It is recorded here because the
natural later request - "show which provider this machine's evidence came from" - would need
the constraint split into a vendor rule and an identity rule, which is a migration rather
than a code change. That is a deliberate trade. The cycle is the scheduler's token for in-process
work, an outside producer has no scheduler dispatch behind it, and a run that both keys
dedup has two answers to "was this a duplicate" and no rule for which wins. A future machine
field on the wire contract is NOT foreclosed: the constraint says nothing about `asset_id`,
so an ingested run may name a machine while keeping the legacy identity.

The identity rules are two separate named constraints rather than one compound constraint
because MySQL reports the constraint name on violation, and two names diagnose two different
producer bugs. This mirrors the `ck_assets_parent_owner_exclusive` precedent, where a
`CHECK` backstops a rule the application also enforces.

A `CHECK` violation raises MySQL error 3819, which `MySqlErrorCode` has no member for. The
write store maps it numerically to a failing `WriteResult` naming the violated constraint. An
unmapped 3819 would escape the store as a `MySqlException`, and the ingest endpoint's
store-failure catch would answer `503 store unreachable` for a permanently invalid write that
the collector would then retry forever.

Alternative considered:

- **An at-least-one identity constraint, keeping a both-identities row legal.** Rejected for
  the reason above: it contradicts the non-contention property that both this design and the
  `evidence-persistence` and `evidence-ingest` deltas state.

### D6. `Error` carries a detail column rather than reusing `raw_payload`

An `Error` run usually has no checks, so `error_detail TEXT NULL` is the only place the
reason can live. `raw_payload` is documented as the vendor's opaque JSON, and overloading it
would make the reason unfindable as a field and would break that column's one meaning.

The column is nullable because only an errored run uses it, but an errored run SHALL fill
it. `ck_evidence_runs_error_detail` enforces both directions, and the store rejects a blank
detail on an `Error` run before any SQL runs. Recording an error with no reason is not
recording it, and a reader that has to treat the detail as optional cannot show the reason
at all. The cost is one clause in a constraint the migration adds anyway and one validation
rule. The alternative, a constraint that only forbids a detail on a non-errored run, would
leave the guarantee this design and the proposal both state as prose that the schema does
not hold to.

An `Error` run MAY still carry checks. A collection that observed three checks and then lost
the provider has both a partial observation and a failure, and discarding the observed part
would lose true evidence. What those checks say is not overridden by the error: see D7.

Known liability: no page renders `error_detail` yet. It is returned on `EvidenceRunRow` and
read back by `GetEvidenceRunsAsync`. The fleet evidence view that shows it is separate work.

### D7. Status precedence is `HardFailure > Errored > Stale > SoftFailure > Passing`

`Errored` sits below `HardFailure` for the reason the existing rule puts `HardFailure`
above `Stale`: a known hard failure is the most actionable signal and is never a false
green, so it outranks a state that means "the answer is not known".

`Errored` sits above `Stale` because it is the sharper and fresher statement of the same
problem. A collector that errored an hour ago is failing now. Reporting it as `Stale`
several cadence windows later, or as `Passing` until then, is exactly the silent green this
change removes.

Unlike the four existing statuses, `Errored` is read from the run-overall `result` column.
The other four are derived from the run's checks and the run's cadence. An errored run
usually has no checks to derive from.

Alternative considered:

- **`Errored > HardFailure > Stale > SoftFailure > Passing`**, on the argument that an error
  means no verdict can be trusted. Rejected. The two orders differ only when one assessed
  set holds both an observed hard failure and an error, which is a mixed cycle (one machine
  failed policy, another could not be reached) or a partially observed run. In that case
  putting `Errored` first downgrades a confirmed, non-repudiable policy breach from a
  failing state to a degraded one, and `web-ux-conventions` S3 reserves red for exactly that
  breach. Hiding an unknown behind a confirmed failure costs less than hiding a confirmed
  failure behind an unknown: both states demand the reader open the collector, and only the
  second one lets a real breach read as amber. An error also never trumps an observation
  that was actually made - the failing check was observed, and only the unreached part is
  unknown.

Accepted residual: in a mixed cycle the errored machine is masked until the hard failure is
resolved, after which the next cycle surfaces `Errored`. The error is not lost in the
meantime: the scheduler records its own `error` status and the run keeps its `error_detail`.

### D8. A cycle is one assessment: the assessed set is cycle-wide, not one run

The derivation keeps its existing grouping key, `(organisation, requirement, collector)`,
and its existing latest-run pin. It then chooses an assessed set:

1. Pin the group's latest run by `collected_at, received_at, created_at, id` descending, as
   today.
2. If that run has a non-null `cycle_id`, the assessed set is every run in the group sharing
   that `cycle_id`. Otherwise the assessed set is that one run, which is exactly today's
   behavior.
3. The status is the worst status across the assessed set under D7's precedence, evaluating
   each run for its own `Error` result, its own failing checks, and its own staleness.
4. `LastCollectedAt` stays the pinned run's `collected_at`, which is the maximum of the set.

This gives three properties. One errored or failing machine is never hidden by a passing
sibling. A machine that has left the fleet drops out on its own, because the newest cycle
defines the current machine set and a machine absent from it contributes nothing. A
retired machine therefore cannot pin a collector to `Stale` forever.

The returned row shape is unchanged, so neither page needs a new lookup key.

The database picks the assessed set, rather than the application folding it out of a
full-history fetch. Today the rollup selects every collector-kind run for the requested
organisations, orders them, and groups in memory. Multiplying that by a fleet is the point at
which the shape stops being affordable, so the query changes with the derivation:

1. A derived column computes each run's effective collector id. It is `collector_id` when
   that column is non-null AND non-empty, and otherwise the prefix of `collector_ref` before
   the first `:`. All three of the C# helper's absence cases are reproduced: an empty
   `collector_id` is absent rather than an identity, a reference with no `:` yields nothing,
   and a leading `:` yields nothing rather than an empty identity. A plain `COALESCE` would
   get the first of those wrong, because `COALESCE` reads `''` as a present value. This is
   the ratified fallback rule, moved into SQL. The C# helper it replaces is deleted, so the
   rule lives in ONE PLACE IN PRODUCTION CODE. The web test fake keeps a copy on purpose,
   because it stands in for the store with no database behind it, and that copy is a
   deliberate, marked duplicate rather than a second statement of the rule.
2. `ROW_NUMBER() OVER (PARTITION BY organisation_id, requirement_id, <effective collector>
   ORDER BY collected_at DESC, received_at DESC, created_at DESC, id DESC)` pins each
   group's latest run and its `cycle_id`. The ordering is the ratified one, unchanged.
3. The statement returns the assessed set only: the pinned run alone when its `cycle_id` is
   null, otherwise every run of the group carrying that `cycle_id`.
4. Each returned run carries two aggregated booleans, "has a failing hard check" and "has a
   failing soft check", each an `EXISTS` over `evidence_checks`. That folds the second query
   into the first. `uq_evidence_checks_evidence_name` serves both lookups through its
   leftmost `evidence_id` prefix.

The application then folds staleness and the precedence over the returned rows. Staleness
stays in C# because the window and grace come from `CollectorFrequency`, and duplicating the
cadence table in SQL would create the second vocabulary the scheduler capability forbids.

The rows crossing the wire drop from every historical run to the newest cycle of each
collector, and the check read stops being a second round trip. The server-side row scan is
unchanged: the statement still reads the same rows under the existing
`(organisation_id, requirement_id, collected_at)` index, and a window function cannot skip
them. Bounding that scan needs a different index, which no read today justifies.

Alternatives considered:

- **Keeping the in-memory fold and adding the cycle grouping in C#.** Rejected. It needs no
  new SQL, but it multiplies the fetched set by the fleet size on a read that already fetches
  all history, and it keeps the check query's `IN` list growing with the fleet. The windowed
  statement is the same contract with a bounded result set.
- **Deriving staleness in SQL too, so one row per collector comes back.** Rejected. The
  window and the grace per cadence token live in `CollectorFrequency`, and restating them in
  SQL would create a second cadence vocabulary that could drift from the first.
- **One status row per machine.** Rejected for this change. It multiplies the batch read by
  the fleet size and forces both pages and the drawer anatomy to change shape, for a fleet
  view that is not being built here.
- **Per-machine latest run, grouping on `(organisation, requirement, collector, asset)` with
  no cycle grouping.** Rejected. It solves the masking problem but not the retirement
  problem: a decommissioned machine's last run keeps contributing forever and pins the
  collector to `Stale` with no way for the fleet to correct itself. It also changes the
  returned row key, which the cycle-wide rule avoids.
- **Deriving `HardFailure` from the run-overall `result` instead of from checks.** Rejected.
  The ratified requirement pins the derivation to the checks, and a directly appended run
  can disagree with its own checks.

### D9. `Errored` reaches the reader two ways, and the row's words are ratified rather than tolerated

The two surfaces that show a collector's status render through different machinery, so the
decision has two halves.

**The control anatomy uses the product vocabulary.** In `ControlDetailProjection` the state
reaches the viewer as a `StatusKind` seal. S1 fixes the product vocabulary (Passing, Failing,
Due soon, Overdue, Drifting/Degraded, Snoozed, Waiting, Draft, Out of scope), and `Errored`
introduces no member: it maps to `StatusKind.Drifting` (the warn seal) with the note
"Collection failed". `Stale` keeps "Collection stopped". S2 holds because the seal carries a
word and a shape. S3 holds because the state is amber, and red stays reserved for `Failing`,
which only a hard failure reaches, because an error is not a policy breach.

**The Statement of Applicability row writes its own words, and this change ratifies that.**
The row badge is not a `StatusKind`. It is an inline `switch` writing text directly, and the
words it writes today - "hard failure", "collection stopped", "soft failure", "passing",
"not collected" - are mostly outside the S1 vocabulary. Adding "collection failed" extends
that set by one word.

That the deviation predates this change is not a reason to leave the rule alone. The
`statement-of-applicability` delta RATIFIES the row's wording: it requires "collection
failed", "collection stopped", and "not collected" as three distinct rendered states.
Ratifying those words while S1 says a status uses only the product vocabulary would leave two
ratified rules disagreeing about the same badge, and a later reader would have to guess which
one governs. A rule that the product deliberately does not follow is worse than no rule.

This change therefore amends S1 with a bounded carve-out. The delta permits an
evidence-status label set on exactly one surface - the collector row badge on the Statement
of Applicability page - and enumerates the six permitted labels, so the set is closed and a
seventh state has to amend the rule rather than invent a word at the page. The carve-out
suspends nothing else: S2 still requires shape plus word, and S3 still holds red for
`hard failure` alone. Every other surface, the control anatomy included, keeps the product
vocabulary.

The carve-out is written to govern the LABEL, not every word about collection on the screen.
The anatomy keeps the product status and adds the note "Collection failed" beside it, exactly
as it already notes "Collection stopped" for a stale collector. A note is not a status label,
so the delta says plainly that a supplementary note in plain words is permitted anywhere. The
alternative was to drop the note and leave an errored collector showing only the warn seal,
which would make it indistinguishable from a stale one on that surface and would lose the
distinction this change exists to create.

`src/Freeboard/stories/UxRules.mdx` restates S1 word for word and is the visual reference the
repo rules point at as tracking the spec, so it takes the same amendment. Left alone it would
state the un-amended rule beside a page that follows the amended one.

The justification for the carve-out is that the row answers a different question from the
rest of the product. The product vocabulary names the state of an OBJECT. The row names the
state of a COLLECTION, and "the collection attempt failed" and "the collector stopped
reporting" have no product-vocabulary member. Folding both into Drifting/Degraded would erase
the distinction that this whole change exists to create.

**Both maps must gain the state.** The page switch sends `Stale` to `badge-brand` while the
projection sends it to `StatusKind.Drifting`. Both render for the same control, so `Errored`
MUST be added to both. Added to one only, it falls through the other's default and reads as
"not collected", which is the false green this change exists to remove. The task list names
both files and a test covers each. Reconciling the two maps into one is pre-existing debt
this change still does not take on.

Alternatives considered:

- **Rendering an errored collector as the product `Failing` state with a "collection
  errored" note, keeping `Errored` only in persistence.** Rejected. It paints red for a
  state that observed no policy breach, which S3 forbids, and it makes a real failure and an
  unreachable provider indistinguishable at a glance - the exact conflation the third result
  value exists to end.
- **Restating the row in S1 vocabulary, so no carve-out is needed.** Rejected for this
  change. It rewrites all five existing row states and their tests, which is a page-rendering
  change this proposal does not take on, and it would still have to answer what word replaces
  "collection stopped". The carve-out records the deviation as a ratified, closed set instead
  of leaving it as unnoticed debt.
- **Leaving S1 alone and recording the deviation only in this design document.** Rejected.
  The `statement-of-applicability` delta ratifies the words, so the disagreement would be
  between two ratified specs rather than between a spec and an accident.

### D10. The migration is one multi-clause `ALTER TABLE`, if MySQL 8.4 accepts the shape

Migration `024` is written as one `ALTER TABLE` so MySQL 8.4 InnoDB atomic DDL applies or
rolls back the whole shape change as a unit, never some columns added and others not.

The single-statement form is the goal, not a proven fact, and it must be proven before the
migration is committed. `018` is a precedent for the idiom but not for this shape: its adds
were metadata-only, and this statement combines a VIRTUAL generated column with a unique key
over that same column, two `MODIFY` clauses on a column an existing unique key covers, and
four `ADD CONSTRAINT CHECK`. InnoDB restricts which of those may share one in-place `ALTER`,
and adding a virtual column together with an index on it is one of the restricted
combinations. The task list therefore runs the exact statement against MySQL 8.4 first.

That trial answers two questions, not one. The first is whether the server accepts the single
statement at all. The second is which ALGORITHM the server chose to serve it, which decides
what the migration costs an operator. Acceptance does not imply `INPLACE`. A statement that
adds a unique key over a virtual generated column and also modifies two columns an existing
unique key covers is served by `ALGORITHM=COPY`, and a copy is not merely slower.
It rebuilds the table into a new file and BLOCKS concurrent writes for the whole rebuild,
including every HTTP evidence ingest, whereas `INPLACE` with `LOCK=NONE` lets writes continue.
The trial therefore asks for `ALGORITHM=INPLACE, LOCK=NONE` explicitly and records the answer.
The server refuses that request with error 1845 when it cannot serve the change that way,
which is the signal that the shipped statement will copy. The migration then ships with no
`ALGORITHM` clause, so the server picks a supported one, and the header states which one the
trial observed and whether writes are blocked while it runs.

If MySQL refuses the combination, the migration splits into two statements in the one file:
the columns first, then the key and the constraints. One further refusal is possible inside
that split, and it needs a third statement: `asset_key` reads `asset_id`, so a server that
declines a generated column whose expression names a column added in the same statement needs
`asset_id` added ahead of it. The split is then `asset_id`, the remaining columns, and last
the key and the constraints. The file still applies as one migration
version, but the all-or-nothing property this decision leans on is lost. A crash between the
two statements then leaves the columns present and the key absent, which the header's
recovery already covers: drop the added columns, key, and constraints, then re-run. Take the
split only if the single statement is refused, and record which form shipped in the header.

The six append-only triggers are untouched. They are `FOR EACH ROW SIGNAL` statements that
name no column, and `ALTER TABLE ... ADD COLUMN` does not drop a trigger. A test asserts all
six still exist after `024`.

Like `015` and `018`, the migration is NOT atomically replay-safe, because the runner records
the schema version only after the SQL succeeds and plain `ADD COLUMN` is not idempotent. The
header documents the recovery: drop the added columns, the added key, and the added check
constraints, then re-run, or record the version by hand.

No row is backfilled and no `UPDATE` runs against `evidence_runs`. The append-only trigger
is the point of the table, and inventing a machine or a cycle for a historical run would
fabricate history.

### D11. A cycle-keyed run is final for that cycle

The cycle key plus the append-only guarantee mean a run appended under
`(cycle_id, organisation_id, requirement_id, asset_key)` can never be replaced, corrected, or
superseded while that cycle id is in force. A retry after a failed dispatch reuses the same
`current_run_id` (D2 property 2), so a later, better answer for the same machine collides
with the earlier one and is discarded.

A producer SHALL therefore append a run only for an outcome it will not retry under the same
cycle id. Concretely, a transient failure that the scheduler will retry - a timeout, a
throttle, a dropped connection - must NOT be appended as an `Error` run, because the
succeeding retry could not overwrite it and the collector would show `Errored` on evidence
that was superseded minutes later. An `Error` run is for a failure that ends the cycle's
attempt for that machine, such as an unresolvable credential or a provider rejection that a
retry will repeat.

Nothing writes a cycle-keyed run in this change, so this decision adds no code. It is
recorded here and in the `evidence-persistence` requirement so the runner that becomes the
first producer inherits the constraint rather than discovering it.

### D12. The `collector-scheduler` delta states the token lifecycle, and the service is fixed to meet it

The capability already ratifies that a claimed collector is dispatched with its stable
`current_run_id` "so a future real runner can make its work idempotent on that id", and that
a failed run keeps that token for the retry. It does not state what ends a cycle.

This change makes that unstated behavior load-bearing: ending the cycle is what stops one
cycle's key from blocking the next cycle's appends. The delta states the full token
lifecycle - minted at claim when absent, retained on failure, cleared by a completion that
still holds its lease, cleared again on a config-change revival - and states what the token
identifies.

The store already does its part. The service does not, so this change edits
`CollectorSchedulerService` as D2 describes. The scope is three edits in one method. The
fenced `CompleteSuccessAsync` is attempted whenever the runner returned normally, including
under a cancelled linked token. The early return on a cancelled token is kept and moved below
that success arm, so it guards only a dispatch whose runner threw under cancellation, and so
a cancellation never reaches `CompleteFailureAsync`. Both completion writes take a fresh
`CancellationTokenSource` with a short timeout rather than the host stopping token. The
scheduler still writes no evidence, and the runner seam is unchanged.

The bounded token is deliberate, and `CancellationToken.None` was the wrong answer. The
completion write has to survive host shutdown, which is the whole point of taking it off the
stopping token, but an uncancellable write on a stalled MySQL connection would hold a
`BackgroundService` open until the host force-stops it. A fresh source with a small timeout
gives both: shutdown does not cancel the write, and a wedged connection cannot outlast the
timeout. The timeout is a local constant rather than a new option, because there is one call
site pair and no operator has a reason to tune it.

This touches a ratified `scheduler-lease` scenario, so that capability gets a delta too. The
scenario "A lost lease cancels the in-flight dispatch" reads "the worker cancels the in-flight
dispatch INSTEAD OF completing it", and after this change the worker cancels and then attempts
a fenced completion. The two are reconcilable, and the delta says how rather than leaving a
reader to infer it: the completion the worker attempts names its own lease token, the row
carries the new holder's token, so the write matches no row and no run outcome is recorded.
The worker still does not complete the run. What changed is that the attempt is made and
declined by the fence, rather than skipped by an early return. The delta restates the scenario
in those terms and adds the rule that a dispatch cancelled mid-run records no failure either,
which is the same rule this capability states from the other side.

The same delta restates the ratified failure requirement. That requirement says a dispatch
that fails records a failure, increments `failure_count`, and sets `status` to `error`, and
its first scenario reads simply "the runner throws". The cancelled dispatch is an exemption to
that rule, and this change makes the exemption load-bearing rather than incidental, so it is
written down rather than inherited silently: a dispatch fails when the RUNNER reports a
failure, and a dispatch the service itself cancelled is not one, even though the runner threw.
The scenario's `WHEN` narrows to a runner that threw with its token not cancelled. The
behavior is unchanged - the service already skips the failure completion on a cancelled token.

The delta also states the rule a lost lease leaves behind, because it constrains the first
producer: the new holder re-dispatches the same cycle id, so a re-delivered append under a
producer's own cycle id is a replay to accept, not a failure to report. A producer that
reported it as a failure would keep the token alive across every retry until the collector
went `dead`.

Alternatives considered:

- **No delta, on the ground that the dispatch-with-run-id sentence already covers it.**
  Rejected. That sentence ratifies that the token is passed and is stable across a retry. A
  reader could satisfy it with a token that is never cleared, which would silently break
  every append after the first successful cycle. A key that depends on a behavior belongs in
  the spec that owns the behavior.
- **A spec-only delta, leaving the service paths as they are.** Rejected. The spec would then
  state a lifecycle the code does not implement on two of its three paths, which is the
  failure the delta exists to prevent.

### D13. Every enum-like comparison this change moves into SQL declares a binary collation

`evidence_runs.result`, `evidence_runs.kind`, `evidence_checks.severity`, and
`evidence_checks.result` are declared with no explicit collation, unlike the id columns beside
them, which all declare `utf8mb4_bin`. They therefore inherit the server default, and on
MySQL 8.4 that is `utf8mb4_0900_ai_ci` - case-insensitive and accent-insensitive.

That default is harmless while the comparisons live in C#, where the store uses
`StringComparison.Ordinal`. This change moves comparisons into SQL, and it is the move that
creates the bug. Under the inherited collation, `ck_evidence_runs_result` would admit a row
whose `result` is `error`. `DeriveStatus` compares ordinally, so it would read that row as not
errored, and `ck_evidence_runs_error_detail` would then demand a detail for a value the
derivation does not recognize. The same slippage would make the rollup's two `EXISTS`
aggregations count a check whose `severity` is `hard` as a hard failure, where the C# they
replace does not.

Every comparison this change writes therefore declares `COLLATE utf8mb4_0900_bin` on the
compared literal: the two new check constraints that compare a literal, the two check
aggregations, and the `kind` predicate the rewritten status query carries. The other two new
constraints test only `IS NULL` and `IS NOT NULL` and compare no string, apart from the
non-blank tests the identity rules carry. Those tests take the introducer but need no
collation, because after `TRIM` an empty-string test cannot differ by case or by padding. `COLLATE` binds
tighter than `=`, and an explicit collation on the literal wins over the column's implicit
one, so `result = _utf8mb4'Error' COLLATE utf8mb4_0900_bin` is a binary comparison.

The collation is `utf8mb4_0900_bin` rather than the `utf8mb4_bin` the id columns beside these
declare, because only one of the two is NO PAD. `utf8mb4_bin` is a PAD SPACE collation, so
trailing spaces are not significant in a nonbinary string comparison and `result = _utf8mb4'Error'
COLLATE utf8mb4_bin` is true for `'Error '`. That collation would reject `error` and still
admit `Error `, and `DeriveStatus` compares with `StringComparison.Ordinal`, which matches
neither. `utf8mb4_0900_bin` is NO PAD, so the SQL comparison and the ordinal one accept
exactly the same values. The id columns keep `utf8mb4_bin`: they carry exact-byte ids, no id
is written with a trailing space, and changing an existing column's collation is a table
rebuild this change has no reason to pay for.

Each literal is written with the `_utf8mb4` introducer. A literal with no introducer takes the
character set of the session that ran the DDL, and a client connected as `latin1` fails the
statement with error 1253, because `utf8mb4_0900_bin` is not a collation of that character
set. The introducer makes the constraint text independent of the migrating client.

The alternative was to move the four columns to a binary collation instead, which would make every
comparison binary by default and could not be forgotten by a later query. It was rejected on
blast radius. Two of the four columns are on `evidence_checks`, so the fix would need a second
`ALTER TABLE` against the largest table in the evidence core, it would rebuild that table for
a change no reader has asked for, and it would cost D10's all-or-nothing property, since one
migration file would then carry two table rebuilds instead of one. Collating the literals
changes comparison only. It rewrites no row, touches no second table, and keeps the migration
to the one table this change already alters.

The residual is real and is accepted: the rule lives in each predicate rather than in the
schema, so a future query against these columns has to remember it. The requirement is
therefore written into the `evidence-persistence` spec as a rule about the columns rather than
about these particular statements, and integration tests insert lowercase and trailing-space
values through raw SQL to prove both halves - the closed set still rejects them, and the
derived status does not move.

## Risks / Trade-offs

- **The migration may block writes to `evidence_runs` while it runs.** -> Unlike the
  metadata-only column adds in `015` and `018`, this statement is not `INSTANT`. It adds a
  unique key over a virtual generated column and modifies two columns an existing unique key
  covers, so the server may well serve it with `ALGORITHM=COPY`, which rebuilds the table and
  rejects concurrent writes for the whole rebuild - HTTP evidence ingest included. Whether it
  copies is measured before the migration is committed, not assumed (D10), and the header
  states the observed algorithm and the write impact rather than only the elapsed cost. The
  table is append-only history and the product is pre-release, so it is small today, and
  `freeboard system migrate` is already an operator-run CLI step rather than a startup
  action, so the blocking window is one an operator schedules.
- **MySQL 8.4 may refuse the single-statement migration shape.** -> Proven against a real
  MySQL 8.4 before the migration is committed. If it is refused, the file splits into two
  statements, or three when the generated column must follow the column it reads, and gives
  up the all-or-nothing property, which the header's recovery covers. See D10.
- **The new unique key costs a write on every insert.** -> `uq_evidence_runs_cycle` is
  maintained on every append, including every HTTP ingest, and every existing row is
  NULL-leading in it. No read uses it. That cost buys the second idempotency key, which is
  the point of the change, but it is a real cost on an append-hot table and the migration
  header states it.
- **The rollup's cost grows with fleet size.** -> Per-machine evidence multiplies the
  assessed set by the fleet size, and the cycle-wide rule turns one check evaluation per
  collector into one per machine in the newest cycle. D8 answers both by moving the pin into
  SQL: a window function pins the latest run per group, the statement returns only the pinned
  cycle's runs, and the check outcomes aggregate server-side into two booleans per run. The
  result set is therefore bounded by the newest cycle's fleet size rather than by history,
  and the second check query disappears. The residual is the server-side row scan: the
  statement still reads every collector-kind run for the requested organisations under the
  existing `(organisation_id, requirement_id, collected_at)` index, because a window function
  cannot skip the rows it partitions. That scan is pre-existing, but the fleet multiplier on
  it is new to this change, so leaving it unaddressed was not an option. Bounding it needs a
  different index, which no read today justifies.
- **The rollup query becomes markedly harder to read.** -> One statement now carries the
  collector-identity fallback, the pin, the assessed-set selection, and the check
  aggregation. It replaces two queries plus an in-memory fold, so the total is not larger,
  but the hard part moves into SQL where it is less testable in isolation. The integration
  tests cover the derivation against a real MySQL, and the C# helper the SQL replaces is
  deleted so production code states the fallback rule once. The web test fake keeps its own
  copy, which is a deliberate double for a store it cannot call.
- **Four enum-like columns inherit a case-insensitive server collation.** -> Every
  comparison this change puts into SQL collates its literal `utf8mb4_0900_bin`, which is
  binary and NO PAD, so the SQL accepts exactly what the ordinal comparison it replaces
  accepts (D13). The columns themselves are left alone, so the rule
  has to be remembered by any later query against them. A raw-SQL integration test pins both
  halves of the behavior.
- **Nothing writes a machine-scoped or `Error` run yet.** -> The capability is unexercised by
  any live path on merge, which is the code-as-liability cost of a foundation change. It is
  bounded by keeping this change to the storage and the rollup, and it is covered by
  persistence integration tests that append through the real store against a real MySQL.
- **`error_detail` has no reader.** -> One nullable `TEXT` column and one read-model member.
  Recording an error with no reason is not recording it, so the alternative is worse.
- **Two divergent status-to-badge maps already exist for one control.** ->
  `StatementOfApplicability.cshtml` maps `Stale` to `badge-brand` while
  `ControlDetailProjection` maps it to `StatusKind.Drifting`. `Errored` must be added to both
  or it silently falls through to "not collected" in one of them. The task list names both
  files, and a test covers each. Reconciling the two maps is pre-existing debt this change
  does not take on.
- **Relaxing `vendor` and `collector_ref` to nullable weakens a live idempotency key.** ->
  Closed by the two check constraints in D5 and by store validation, so a half-identified run
  cannot be stored and every run falls under one of the two keys. The consumer set of the
  read-model change is fully enumerated in D4 and no page reads either member.
- **Deriving `Errored` from `result` while the other statuses derive from checks is a split
  rule.** -> Unavoidable, because an errored run usually has no checks. The read store doc
  comment and the spec both state which input each status comes from.
- **The cycle key makes a run unrepairable within its cycle.** -> Stated as D11 and carried
  into the `evidence-persistence` requirement, so the first producer inherits the rule. Until
  a producer exists, no path can violate it.
- **The change edits a live scheduler path that no evidence producer exercises yet.** -> The
  completion fix changes what a shutdown or a lost lease records for a collector, and the
  only runner today is the no-op. The gain is that the token lifecycle the key rests on is
  true before a producer relies on it. The edit is three statements in one method, the fence
  is what makes the completion safe, and the retained early return is what keeps a cancelled
  dispatch off the failure path. The scheduler orchestration tests cover all three outcomes
  with the in-memory fake. The visible behavior change is narrow: a dispatch whose runner
  RETURNED NORMALLY as the host stopped now records success and schedules its next run,
  instead of leaving the row `running` until its lease lapses. A dispatch whose runner
  honored its token and threw records nothing, exactly as it does today.
- **The exclusive identity constraint forecloses an ingested run that carries a cycle.** ->
  Deliberate, recorded in D5. Widening it later is a migration, not a code change. A machine
  field on the wire contract stays possible, because the constraint says nothing about
  `asset_id`.

## Migration Plan

1. Before applying, run `SELECT DISTINCT result FROM evidence_runs;`. MySQL validates the
   existing rows when it adds an enforced `CHECK`, so a recorded run outside `Pass` and
   `Fail` fails the whole `ALTER`. Only `ck_evidence_runs_result` can plausibly fail this
   way, because the result set was enforced in C# alone until now. A row outside the set is
   a defect to investigate, not to migrate around.
2. Apply `024` with `freeboard system migrate`. It is additive and rewrites no row.
3. Every existing row reads back `asset_id`, `cycle_id`, and `error_detail` as null, and
   `asset_key` as the empty string. Every existing row keeps its `vendor` and
   `collector_ref` and has a null `cycle_id`, so it satisfies the exclusive identity
   constraint through the legacy arm with no repair.
4. No backfill. No `UPDATE` runs against `evidence_runs`, so the append-only `BEFORE UPDATE`
   trigger is never in the way.
5. Every existing run has a null `cycle_id`, so every existing collector falls into D8's
   single-run branch and keeps exactly the status it had before the migration.
6. Rollback, if needed before any cycle-keyed run is written: drop `uq_evidence_runs_cycle`,
   the four check constraints, and the four added columns, then restore
   `vendor`/`collector_ref` to `NOT NULL`. After a cycle-keyed run exists, restoring
   `NOT NULL` would need those rows removed, which the append-only delete trigger forbids.
   Roll forward instead.

## Open Questions

1. Should a scheduler dispatch failure append an `Error` run in addition to setting the
   scheduler's `error` status? This change makes it possible and deliberately does not do
   it. D11 constrains the answer: only a failure that ends the cycle's attempt may be
   appended. The `integration-connection` capability's unresolvable-token rule is the first
   case that has to decide.
2. Should the HTTP ingest contract eventually gain an optional machine field, so an agent
   collector can name the host it ran on? It is a natural fit, and it is a separate change
   against a frozen, externally consumed contract. D5's identity constraint leaves it open.
   An ingested run carrying a CYCLE is a different question and is foreclosed by that same
   constraint.
3. `Errored` and `SoftFailure` both render as a warn badge, distinguished by text and by the
   `data-collector-status` hook. The `web-ux-conventions` delta permits both labels and keeps
   S2 in force, so each carries a shape and a word. Confirm the two are distinguishable
   enough in practice, or pick a different tone for one of them.
4. Should the two status-to-badge maps be reconciled into one shared mapping? They already
   disagree about `Stale`, and every new status has to be added twice. It is pre-existing
   debt, and folding it in here would widen this change's blast radius into page rendering.
   Whether the row should speak the product vocabulary is no longer open. The
   `web-ux-conventions` delta ratifies the row's six evidence-status labels as a closed set on
   that one surface.
