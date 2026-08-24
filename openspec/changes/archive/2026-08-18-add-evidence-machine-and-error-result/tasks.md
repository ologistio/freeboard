## 1. Schema: the machine dimension, the collection cycle, and the error result

Commit: `feat(persistence): add the machine, cycle, and error columns to evidence runs`

- [x] 1.1 Prove the migration statement against a real MySQL 8.4 before you write the file.
  The test compose file provides a server. Run the exact `ALTER TABLE` of 1.2 by hand.
  Run it twice. Run it first with `ALGORITHM=INPLACE, LOCK=NONE` appended. Then run it with
  no `ALGORITHM` clause. Record both answers.
  - InnoDB restricts which changes may share one in-place `ALTER`. This statement combines
    four restricted things. It adds a VIRTUAL generated column. It adds a unique key over
    that same column. It modifies two columns that an existing unique key covers. It adds
    four check constraints.
  - If the server accepts the statement, keep the single-statement form. State in the header
    that the file specifies no `ALGORITHM`, so the server chooses a supported one.
  - If the server refuses the statement, split the file into two statements. Put the columns
    first. Put the unique key and the constraints second. State in the header that the
    migration is no longer all-or-nothing. A crash between the two statements then needs the
    documented recovery.
  - If the server still refuses the columns statement because `asset_key` reads `asset_id` in
    the statement that adds `asset_id`, split three ways. Put `asset_id` first, the remaining
    columns including `asset_key` second, and the unique key and the constraints third. Take
    this branch only on that refusal, and record which form shipped.
  - Record which algorithm the server actually chose. Error 1845 on the first run means the
    server cannot serve the change in place. The shipped statement then copies the table.
    A copy blocks every concurrent write to `evidence_runs` for its whole duration. HTTP
    evidence ingest is one of those writes. Expect error 1845 here. MySQL 8.4.10 refuses this
    statement under `ALGORITHM=INPLACE, LOCK=NONE` and accepts it with no clause, so the
    shipped statement copies the table and blocks ingest writes. This run confirms that
    measurement. A different answer means the server differs, so check it before you rely on
    it. Carry this answer into 1.3.
- [x] 1.2 Add `src/Freeboard.Persistence/Migrations/024_evidence_machine_and_error.sql`
  as ONE multi-clause `ALTER TABLE evidence_runs`, or as the split of 1.1:
  - `ADD COLUMN asset_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL
    AFTER requirement_id`
  - `ADD COLUMN cycle_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL`
  - `ADD COLUMN error_detail TEXT NULL`
  - `ADD COLUMN asset_key VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
    GENERATED ALWAYS AS (COALESCE(asset_id, _utf8mb4'')) VIRTUAL`
  - `MODIFY vendor ... NULL` and `MODIFY collector_ref ... NULL`. Keep the existing type and
    collation on both.
  - `ADD UNIQUE KEY uq_evidence_runs_cycle (cycle_id, organisation_id, requirement_id,
    asset_key)`
  - `ADD CONSTRAINT ck_evidence_runs_result CHECK (result IN (_utf8mb4'Pass' COLLATE
    utf8mb4_0900_bin, _utf8mb4'Fail' COLLATE utf8mb4_0900_bin, _utf8mb4'Error' COLLATE
    utf8mb4_0900_bin))`
  - `ADD CONSTRAINT ck_evidence_runs_error_detail CHECK ((result = _utf8mb4'Error' COLLATE
    utf8mb4_0900_bin AND error_detail IS NOT NULL AND TRIM(error_detail) <> _utf8mb4'') OR
    (result <> _utf8mb4'Error' COLLATE utf8mb4_0900_bin AND error_detail IS NULL))`
  - `ADD CONSTRAINT ck_evidence_runs_ref_pair CHECK ((vendor IS NULL AND collector_ref IS
    NULL) OR (vendor IS NOT NULL AND collector_ref IS NOT NULL))`
  - `ADD CONSTRAINT ck_evidence_runs_cycle_identity CHECK ((vendor IS NOT NULL AND
    collector_ref IS NOT NULL AND cycle_id IS NULL) OR (collector_id IS NOT NULL AND
    TRIM(collector_id) <> _utf8mb4'' AND cycle_id IS NOT NULL AND TRIM(cycle_id) <>
    _utf8mb4'' AND vendor IS NULL AND collector_ref IS NULL))`
  - The cycle arm tests both columns for a non-blank value, not only for a non-null one. An
    empty `collector_id` names NO collector to the read side, which reads an identity only
    from a non-empty value. A row with an empty `collector_id` and a `cycle_id` would satisfy
    a null-only identity check and then produce no collector status at all.
  - The identity form is EXCLUSIVE, not at-least-one. An at-least-one form admits a row that
    carries the legacy pair AND a cycle. That row lands in both unique keys.
  - Collate every compared literal `utf8mb4_0900_bin`, and write each literal with the
    `_utf8mb4` introducer. The `result` column declares no collation, so it inherits the
    server default `utf8mb4_0900_ai_ci`. A bare literal would let `ck_evidence_runs_result`
    admit `'error'`. `DeriveStatus` compares ordinally and would read that row as not errored.
  - Use `utf8mb4_0900_bin`, NOT `utf8mb4_bin`. Both are binary, but `utf8mb4_bin` is a PAD
    SPACE collation, so it reads `'Error '` as equal to `'Error'` and admits a trailing space
    the ordinal C# comparison does not match. `utf8mb4_0900_bin` is NO PAD and matches the
    ordinal comparison exactly. The new id columns keep `utf8mb4_bin`, which is the repo's
    id-column convention from `011`. Only the compared literals take the NO PAD collation.
  - The `_utf8mb4` introducer fixes the literal's character set. Without it the literal takes
    the migrating client's `character_set_connection`, and a client connected as `latin1`
    rejects the statement with error 1253 (the collation is not valid for that character set).
    The introducer applies to the empty-string literals too.
  - Use `TRIM(error_detail) <> _utf8mb4''`, not `error_detail <> _utf8mb4''`. The rule is
    non-blank. A constraint that rejects only the empty string admits a single space, which
    the store rejects.
  - Do NOT drop, rebuild, or condition `uq_evidence_runs_vendor_collector_ref`. It stays
    unconditional. A cycle-keyed run leaves both its columns null. MySQL treats each `NULL`
    as distinct, and the identity constraint keeps the two key populations disjoint.
- [x] 1.3 Write the migration header comment in the style of `015` and `018`. State each of
  the following:
  - Why `asset_id` carries no foreign key.
  - Why the key needs the generated `asset_key` rather than `asset_id`. MySQL treats each
    `NULL` in a unique index as distinct.
  - Why `collector_id` is not in the key. It is redundant, because the scheduler mints a
    `cycle_id` on one collector's state row. A fifth `VARCHAR(190)` part would also take the
    key from 2384 to 3144 bytes, past the InnoDB 3072-byte limit.
  - Why the legacy key survives untouched while its columns turn nullable.
  - What the two identity constraints stop, and why the second one is exclusive.
  - Why every compared literal declares `COLLATE utf8mb4_0900_bin` with the `_utf8mb4`
    introducer, why that NO PAD collation rather than the PAD SPACE `utf8mb4_bin`, and why
    the new id columns still declare `utf8mb4_bin`.
  - That no `asset_id` index is added, matching `015`.
  - That `uq_evidence_runs_cycle` is a new index on an append-hot table. It costs an index
    write on EVERY later insert, including every HTTP ingest. It is NULL-leading on every
    existing row and serves no read today.
  - That MySQL validates the recorded rows against each new `CHECK`. An operator therefore
    runs `SELECT DISTINCT result FROM evidence_runs;` before applying.
  - That no trigger is dropped or re-created, and that no row is backfilled.
  - Which statement form 1.1 proved.
  - Which ALTER algorithm the server chose for that form, from 1.1. State the DML impact,
    not only the elapsed cost. Under `ALGORITHM=COPY` the table rebuild blocks every
    concurrent write to `evidence_runs`, including every HTTP ingest. Under
    `ALGORITHM=INPLACE` with `LOCK=NONE` those writes continue.
  - The operational recovery for the non-replay-safe window. Drop the added columns, key,
    and constraints, then re-run. An operator may instead record the version by hand.
- [x] 1.4 Do NOT add an index on `asset_id`. Do NOT touch the six append-only triggers.
- [x] 1.5 Extend `tests/Freeboard.Persistence.Tests/EvidenceIntegrationTests.cs` with
  migration-shape assertions read from `information_schema`. Assert each of the following:
  - The four new columns exist with the stated nullability.
  - `asset_key` is a generated column.
  - `vendor` and `collector_ref` are now nullable.
  - `uq_evidence_runs_cycle` is unique over exactly
    `[cycle_id, organisation_id, requirement_id, asset_key]`, in that order.
  - The four check constraints exist by name.
  - `evidence_runs` still has no foreign key.
  - All six append-only triggers still exist.
  - `uq_evidence_runs_vendor_collector_ref` is still unique over exactly
    `[vendor, collector_ref]`, with no added key part. This proves the legacy replay contract
    is unchanged. Keep the existing assertion on that key.
- [x] 1.6 Rewrite `Migration015AddsNullableColumnsWithoutBackfill` in the same file. The test
  rebuilds the pre-015 shape with `ALTER TABLE evidence_runs DROP COLUMN collector_id, DROP
  COLUMN frequency;`. MySQL refuses to drop a column that a check constraint names, and
  raises error 3959. `ck_evidence_runs_cycle_identity` now names `collector_id`. Drop that
  constraint before the columns. Restore it after the test re-applies `015`. The test then
  still proves what it was written to prove. `015` adds both columns as nullable and
  backfills nothing. Keep the legacy seed row satisfying the constraints that remain. It
  carries a vendor and a collector reference, and its result is `Pass`.

## 2. Write path: append a machine-scoped, cycle-keyed, or errored run

Commit: `feat(persistence): append evidence runs with a machine, a cycle, and an error result`

- [x] 2.1 Add `AssetId`, `CycleId`, and `ErrorDetail` to `NewEvidenceRun` in
  `src/Freeboard.Persistence/IEvidenceWriteStore.cs`. Add them as trailing optional members
  (`string? ... = null`), so every existing call site keeps compiling. Relax `Vendor` and
  `CollectorRef` to `string?`. Rewrite the record's doc comment. It describes the two
  identities, the rule that a run carries exactly one of them, and the two idempotency keys.
- [x] 2.2 Update the `IEvidenceWriteStore` doc comment. An append fails on a duplicate under
  EITHER key. The run result set is `Pass`, `Fail`, or `Error`.
- [x] 2.3 In `src/Freeboard.Persistence/MySqlEvidenceWriteStore.cs`, add `asset_id`,
  `cycle_id`, and `error_detail` to the `INSERT` column list and to the parameters. Do NOT
  write `asset_key`. The database generates it.
- [x] 2.4 Compute one effective run, validate that run, and insert that same run. Two rules
  build it:
  - Force the kind-scoped members to null. `AssetId` and `CycleId` join `CollectorId` and
    `Frequency` as members a non-`Collector` kind cannot persist. Do NOT force `ErrorDetail`.
    The detail belongs to the run's `result`, which every kind carries, so forcing it null
    would reject an errored append with a "detail required" message after dropping the detail
    the caller supplied. Today the forcing happens at the `INSERT`, after `Validate`.
    Validation therefore passes on the caller's values while the row written carries different
    ones. A row that validation approved could then still break a check constraint.
  - Normalize a blank `Vendor`, `CollectorRef`, `CollectorId`, `AssetId`, `CycleId`, or
    `ErrorDetail` to null. A whitespace-only value is absent to `string.IsNullOrWhiteSpace`
    and present to the check constraints. The two layers would otherwise disagree about the
    same row. Normalizing before validation and before the insert gives absence one
    representation. `asset_key` is generated from `asset_id` under a PAD SPACE collation. A
    blank `AssetId` therefore collides with the organisation-level run of the same cycle. The
    stored row also claims a machine of one space.
- [x] 2.5 Extend `Validate` over the effective run. Add these rules:
  - Accept `Error` for the run result. `IsResult` today answers for both the run result and
    the per-check result. Split the helper in two, so per-check results stay `Pass` or
    `Fail`.
  - Require a non-blank `ErrorDetail` when the result is `Error`. Require a null
    `ErrorDetail` otherwise.
  - Require `Vendor` and `CollectorRef` to be both present or both absent.
  - Require a non-blank `CollectorId` and a non-blank `CycleId` for a cycle-keyed run. Both
    are normalized by 2.4, so the rule is a non-null test over the effective run. Non-null is
    not enough on its own: the read side reads an identity only from a NON-EMPTY
    `collector_id`, so a run with an empty one would store and then produce no collector
    status.
  - Require EXACTLY ONE of the two identities. This rejects a run that neither key can dedup.
    It also rejects a run that both keys would claim. Both are rejected before any SQL runs.
  - REPLACE the `"Evidence vendor is required."` and the `"Evidence collector reference is
    required."` rules with the identity rules above. Delete both. A cycle-keyed run carries
    neither value, so a kept rule rejects every cycle-keyed append before any SQL runs.
  - Replace the `"Evidence result must be 'Pass' or 'Fail'."` message. Keep each rejection
    message specific enough to name the rule that failed.
  - Note the consequence for attestations in the code. Their cycle and machine are forced
    null. An attestation append therefore satisfies the identity rule through `vendor` and
    `collector_ref`, which every attestation path already sets.
- [x] 2.6 Update the duplicate-key catch message. It names both keys, not only the vendor and
  the collector reference. Update the class summary on `MySqlEvidenceWriteStore` in the same
  pass. It says that a duplicate `(vendor, collector_ref)` or a duplicate check name maps to a
  conflict. The store now dedups under two keys, and it maps a broken check constraint to a
  failure as well.
- [x] 2.7 Map a check-constraint violation to a failing `WriteResult`. MySQL raises error
  3819 for a broken `CHECK`, and `MySqlErrorCode` has no member for it. Match the number.
  Name the violated constraint from the exception message in the returned failure. Without
  this the exception escapes the store as a raw `MySqlException`. The ingest endpoint's
  store-failure catch then answers `503 store unreachable` for a permanently invalid write,
  which the collector would retry forever. With 2.4 and 2.5 in place the store cannot reach a
  violation itself. This is the backstop for a caller the store gains later.
- [x] 2.8 Add `AssetId`, `CycleId`, and `ErrorDetail` to `EvidenceRunRow` in
  `src/Freeboard.Persistence/EvidenceReadModels.cs` as trailing optional members. Relax
  `Vendor` and `CollectorRef` to `string?`. Update the record's doc comment.
- [x] 2.9 Correct the comment at `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`. It
  reads "main's `evidence_runs.vendor` is NOT NULL and half the idempotency key". The column
  becomes nullable. State the real reason the endpoint still rejects a collector with no
  vendor. An ingested run is identified by `(vendor, collector_ref)`, and a null vendor would
  leave it outside the replay key. No behavior changes.
- [x] 2.10 Add integration tests to `EvidenceIntegrationTests.cs`. Cover each case:
  - A machine-scoped run round-trips with its `asset_id` and its organisation.
  - Two machines in one cycle both store.
  - Re-appending a run under the same `(cycle_id, organisation, requirement, asset)` with a
    DIFFERENT result conflicts, and the original result is unchanged. A cycle run is final.
  - Two organisation-level runs in one cycle conflict. This is the generated `asset_key` case.
  - Runs of the same machine under two different cycle ids both store.
  - An `Error` run with a detail and no checks stores.
  - An `Error` run that carries partial checks stores.
  - An `Error` run with no detail is rejected.
  - An `Error` run whose detail is a single space is rejected.
  - An error detail on a `Pass` run is rejected.
  - A run with neither identity is rejected.
  - A run carrying BOTH identities is rejected.
  - A run with only one half of `(vendor, collector_ref)` is rejected.
  - A run whose `Vendor` is only whitespace is rejected as a run with no vendor, which proves
    the normalization of 2.4.
  - The existing `(vendor, collector_ref)` replay still conflicts for a run carrying no cycle.
  - A cycle-keyed run whose `CollectorId` is blank is rejected, and one whose `CycleId` is
    blank is rejected.
  - A raw-SQL insert whose `collector_id` is the empty string and whose `cycle_id` is set is
    rejected by `ck_evidence_runs_cycle_identity`.
  - Three raw-SQL inserts prove the constraint compares under a NO PAD binary collation. Use a
    direct `INSERT`, not the store, because the store rejects each one first. Leave
    `error_detail` NULL on all three. Give each row a vendor, a collector reference, and a
    null `cycle_id`, so `ck_evidence_runs_result` is the only constraint the row can break. A
    row with no identity also breaks `ck_evidence_runs_cycle_identity`, and the server may
    then name that constraint instead. Assert the violated constraint the server names is
    `ck_evidence_runs_result`:
    - a run whose `result` is `'error'`
    - a run whose `result` is `'Error '`, with a trailing space
    - a run whose `result` is `'Fail '`, with a trailing space
    The named constraint is what discriminates, so assert it rather than the rejection alone.
    Under a case-insensitive comparison the first row passes `ck_evidence_runs_result`. It
    then reads as errored, carries no detail, and is rejected by
    `ck_evidence_runs_error_detail` instead. Under a PAD SPACE comparison the second row fails
    the same way and the third row stores.
- [x] 2.11 Add endpoint tests to `tests/Freeboard.Web.Tests/EvidenceIngestEndpointTests.cs`.
  Assert each over `FakeEvidenceWriteStore.Appended`, which records the run the endpoint
  appended:
  - An accepted payload appends a run whose `AssetId` and `CycleId` are both null, because the
    wire contract defines neither field.
  - An accepted payload appends a run whose result is `Pass` or `Fail`. Cover the hard-failure
    payload and the passing payload, and assert neither appends `Error`.
  - An accepted payload appends a run whose `Vendor` and `CollectorRef` are both non-null, so
    the replay key still covers every ingested run now that both columns are nullable.
  - A re-POST of the same `collector_id` and `run_id` still answers `200 OK` and appends no
    second run, which proves the cycle key changed no ingest behavior.

## 3. Read path: assess a whole cycle and derive Errored

Commit: `feat(persistence): derive collector status over a collection cycle and surface Errored`

- [x] 3.1 In `src/Freeboard.Persistence/MySqlEvidenceStore.cs`, add `asset_id`, `cycle_id`,
  and `error_detail` to `RunColumns` and to `RunScalar`. Carry them through
  `AssembleRunsAsync` into `EvidenceRunRow`. Declare `RunScalar.Vendor` and
  `RunScalar.CollectorRef` as `string?`. Dapper assigns a null column into a non-nullable
  `string` member without complaint, so the record would otherwise misdescribe its own rows.
- [x] 3.2 Replace the status query's full-history fetch and in-memory grouping. Write one
  statement that returns the assessed set:
  - Derive each run's effective collector id in SQL. It is `collector_id` when that column is
    non-null AND non-empty. It is otherwise the prefix of `collector_ref` before the first
    `:`. Reproduce all three of the C# helper's absence cases. An empty `collector_id` is
    absent, not an identity. A reference with no `:` yields no identity. A leading `:` yields
    no identity rather than an empty one. A plain `COALESCE` gets the first case wrong,
    because `COALESCE` reads `''` as present. Tolerate a now-nullable `collector_ref`.
    Exclude a run with no recoverable identity.
  - Pin each `(organisation, requirement, effective collector)` group's latest run with
    `ROW_NUMBER() OVER (PARTITION BY ... ORDER BY collected_at DESC, received_at DESC,
    created_at DESC, id DESC)`. The ordering is the existing `LatestOrder`, unchanged.
  - Return the pinned run alone when its `cycle_id` is null. Return every run of that group
    carrying the pinned `cycle_id` otherwise.
  - Project the group key onto every returned row: `organisation_id`, `requirement_id`, and
    the derived effective collector id. The caller groups on these three. The derived id must
    come back from SQL, because recomputing it in C# would restate the rule the query owns.
  - Project each returned run's `result`, `frequency`, and `collected_at`.
  - Project the pinned run's `collected_at` for `LastCollectedAt`.
  - Project two aggregated booleans per run. One says the run has a failing `Hard` check. The
    other says it has a failing `Soft` check. Each is an `EXISTS` over `evidence_checks`,
    served by `uq_evidence_checks_evidence_name` through its leftmost `evidence_id` prefix.
  - Collate every compared literal `utf8mb4_0900_bin`, written with the `_utf8mb4`
    introducer, exactly as the migration does. This covers the `kind = _utf8mb4'Collector'`
    filter, the `severity` test, and the `result` test in both `EXISTS` predicates. The three
    columns inherit the server's case-insensitive default collation. A bare literal would
    count a check whose severity is `hard` as a hard failure, which the C# it replaces does
    not. `utf8mb4_bin` would count `hard ` with a trailing space the same way, because it is
    a PAD SPACE collation. `utf8mb4_0900_bin` is NO PAD and matches the ordinal comparison.
  - Delete the now-unused `EffectiveCollectorId(StatusScalar)` helper and the separate
    batched check query, so production code states the fallback rule once.
  - Declare the remaining nullable members of `StatusScalar`, `CollectorRef` included, as
    `string?`.
  - Remove the two-query `RepeatableRead` transaction. The derivation is now one statement,
    so it no longer needs a transaction for a consistent pin. Note in the doc comment that a
    single statement reads one consistent snapshot.
- [x] 3.3 Rewrite `DeriveStatus` to fold over the assessed set. The precedence is
  `HardFailure > Errored > Stale > SoftFailure > Passing`. Read `Errored` from a run's
  `result` column. Read `HardFailure` and `SoftFailure` from the aggregated check flags. Read
  `Stale` from each run's own `collected_at` and `frequency`, through `CollectorFrequency`.
  An errored run's own checks still count, so a failing hard check on an errored run yields
  `HardFailure`. Keep `LastCollectedAt` as the pinned run's `collected_at`. Update the
  comment above the method. State the new order. State why `HardFailure` outranks `Errored`,
  which is that an observed breach is actionable and red is reserved for it. State why
  `Errored` outranks `Stale`.
- [x] 3.4 Update the `IEvidenceStore` and `CollectorEvidenceStatusRow` doc comments. State
  the five emitted statuses. State the assessed-set rule. State that `Unknown` is still
  caller-derived and is never emitted.
- [x] 3.5 Add integration tests to `EvidenceIntegrationTests.cs`. Cover each case:
  - An errored latest run assesses `Errored`, not `Passing` and not `Stale`.
  - A hard failure in the same cycle outranks an error.
  - A failing hard check ON an errored run yields `HardFailure`.
  - An error outranks staleness.
  - One failing machine in a cycle is not hidden by passing siblings.
  - One errored machine in a cycle is not hidden by passing siblings.
  - A machine with runs only in an older cycle does not contribute.
  - A run with a null `cycle_id` is still assessed alone, with exactly the status it had
    before.
  - A pre-migration-shaped run whose `collector_id` is null is still attributed by its
    `collector_ref` prefix. This now proves the SQL fallback rather than the deleted C# one.
  - A run whose `collector_id` is the empty string falls back the same way. A `COALESCE`-
    shaped expression would treat `''` as an identity and group the run under a collector
    that does not exist.
  - A run whose `collector_ref` starts with `:` and whose `collector_id` is null produces no
    status.
  - A raw-SQL check row whose `severity` is `hard` and whose `result` is `fail` does not
    change the derived status. Insert it with a direct `INSERT`, not through the store. This
    proves the aggregation compares under a binary collation, matching the ordinal comparison
    it replaced.
  - A raw-SQL check row whose `severity` is `Hard ` and whose `result` is `Fail `, both with a
    trailing space, does not change the derived status either. A PAD SPACE collation would
    count it as a hard failure.
  - A raw-SQL run whose `kind` is `collector` rather than `Collector` produces no status row.
    The status query's `kind` filter is now case-sensitive, and the store writes only the
    PascalCase name.
  - Keep every existing status test passing unchanged.

## 4. Web read surfaces render the errored state

Commit: `feat(web): render an errored collector distinctly from stale and not collected`

- [x] 4.1 Add the `Errored` case to the badge `switch` in
  `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml`. Use `badge-warn`, the
  text "collection failed", and `data-collector-status="Errored"`. Leave the other four cases
  and the `Unknown` default unchanged.
- [x] 4.2 Add `Errored` to BOTH status maps. The page switch and the projection map are two
  SEPARATE maps for the same control, and they already disagree about `Stale`. A miss in
  either one renders an errored collector as "not collected". Add
  `"Errored" => StatusKind.Drifting` to `MapCollectorStatus`. Add
  `"Errored" => "Collection failed"` to `NoteFor`. Both are in
  `src/Freeboard/Pages/Compliance/ControlDetailProjection.cs`. Do NOT add a `StatusKind`
  member, because the product vocabulary is closed. Update the comment above
  `MapCollectorStatus` to say why an error is a warn seal and not red.
- [x] 4.3 Bring `tests/Freeboard.Web.Tests/FakeEvidenceStores.cs` back in step with the real
  store. Three separate problems:
  - `FakeEvidenceStore.DeriveStatus` carries a copy of the precedence and needs `Errored`.
    The seeding helper needs to set a run result, so a test can stage an errored collector.
    Correct the class doc comment. It claims the fake derives "exactly as the real store
    does", and the real store now assesses a whole cycle. Either implement the assessed-set
    rule in the fake, or say plainly which part the fake does not model.
  - `FakeEvidenceWriteStore` keys its duplicate check on `(run.Vendor, run.CollectorRef)`.
    Once both are nullable, every in-process run collides on `(null, null)` and the fake
    reports a false conflict. Key the set the way the store does. Use the legacy pair when
    the run carries it, and `(cycle_id, organisation, requirement, asset)` otherwise.
  - `EffectiveCollectorId` calls `run.CollectorRef.IndexOf(':')`, which throws a
    `NullReferenceException` on a run with no legacy identity. Guard it and return no
    identity, matching the store. Keep this helper. The fake stands in for a store with no
    database behind it, so it needs its own copy of the rule. Say so in a comment beside the
    helper, so a later reader does not delete it as a duplicate.
- [x] 4.4 Extend `tests/Freeboard.Web.Tests/StatementOfApplicabilityPageTests.cs`. An errored
  collector renders `data-collector-status="Errored"` and the text "collection failed".
  Assert it is distinct from the stale and unknown cases the existing tests cover.
- [x] 4.5 Extend `tests/Freeboard.Web.Tests/ControlDetailPageTests.cs`. An errored proving
  check renders the warn seal and the note "Collection failed". Assert it is distinct from
  the stale note and from the sealless unknown case.

- [x] 4.6 Append the carve-out to the S1 entry in `src/Freeboard/stories/UxRules.mdx`. The
  file restates S1 word for word. It is the visual reference that tracks the rule, so an
  un-amended mirror states a rule the page does not follow. Keep the addition a summary:
  - Name the six evidence-status labels.
  - Say the carve-out covers the Statement of Applicability collector row badge only.
  - Say that S2 and S3 still apply.
  - Leave the spec as the source of truth.

## 5. The scheduler ends a cycle whenever its runner returned normally

Commit: `fix(scheduler): end a collection cycle whenever the runner returned normally`

- [x] 5.1 Reorder the two guards in
  `src/Freeboard/Scheduler/CollectorSchedulerService.DispatchAsync`. Attempt the fenced
  `CompleteSuccessAsync` whenever the runner RETURNED NORMALLY, which means `RunAsync`
  returned without throwing. Attempt it even when the linked token is cancelled. Today an
  early return on the cancelled token runs first and skips that completion.
  - The fence (`WHERE collector_id = @Id AND lease_token = @Token`) is what makes the attempt
    safe. If the lease moved to another holder, the write matches no row and changes nothing.
    The existing lease-lost log still fires.
  - A runner that returns normally under a cancelled token is asserting that it finished its
    work. The completion therefore records `status = 'ok'`, clears `current_run_id`, and
    advances `next_due_at` by a full interval.
  - Replace the comment above the guard. Say why the fence, not an early return, is what
    guards a completion.
- [x] 5.2 KEEP the early return for a cancellation-caused failure. Move it BELOW the success
  arm added by 5.1. It then guards one path only: the runner threw while the linked token was
  cancelled.
  - A runner that honors its token throws `OperationCanceledException`, and `IsFatal` does
    not exclude that type. Such a dispatch is therefore on the FAILURE path.
  - Without the early return it would reach `CompleteFailureAsync`. That call increments
    `failure_count`, sets `status = 'error'`, and applies a backoff. At `MaxAttempts` the row
    turns `dead` and is never claimed again until a config change revives it.
  - Cancelling our own work must not cost a collector its failure budget. Such a dispatch
    therefore records no outcome at all, and the token stays in place for the retry.
  - State this reason in the comment above the guard.
- [x] 5.3 Run both completion writes under a bounded token that host shutdown does not cancel.
  They take `stoppingToken` today, so a shutdown between the runner returning and the write
  cancels the write that ends the cycle. Use a fresh `CancellationTokenSource` with a short
  timeout, such as ten seconds. Do NOT pass `CancellationToken.None`. An uncancellable write
  on a stalled MySQL connection would hold the `BackgroundService` open until the host
  force-stops it. Keep the timeout a local constant, because there is one call site pair and
  no operator needs to tune it.
- [x] 5.4 Do NOT change `MySqlCollectorSchedulerStore`. Do NOT clear the token outside the
  fence. A lost lease means another holder owns the row. That holder re-dispatches the same
  cycle id, which is the retry behavior the capability already ratifies.
- [x] 5.5 Make `tests/Freeboard.Web.Tests/FakeCollectorSchedulerStore.cs` model a lost lease
  the way the database does. `RenewalsReportLost` reports the loss today but leaves the row's
  `lease_token` matching. A fenced completion would therefore succeed against the fake and
  pass a test that fails in production. Rotate the row's lease token when a renewal reports
  the lease lost.
- [x] 5.6 Update `tests/Freeboard.Web.Tests/CollectorSchedulerServiceTests.cs`:
  - `LostLeaseCancelsInFlightDispatchAndSkipsCompletion` now asserts that the completion is
    attempted and changes nothing. The row stays `running` under the new holder's token and
    keeps its `current_run_id`. Rename it to match. Note that its runner swallows the
    `OperationCanceledException` and returns normally, so this test exercises 5.1.
  - `HostShutdownStopsHeartbeatEvenWhenRunnerIsSlow` now asserts the opposite outcome for its
    final check. Its runner returns normally, so the dispatch ends its cycle. The row becomes
    `ok`, with a null `current_run_id` and a rescheduled `next_due_at`. Its heartbeat
    assertions are unchanged. This is the one visible behavior change in this group. State
    that in the test comment as an outcome of the code, with no reference to this plan.
  - Add a test for a runner that RETHROWS `OperationCanceledException` under host shutdown.
    No existing test covers this, because every cancelled runner today swallows the exception
    and returns. Assert the row is NOT `error` and NOT `dead`. Assert `failure_count` is
    unchanged. Assert `current_run_id` is unchanged. `Peek` exposes all three.
  - Add a test for a dispatch that succeeds while the host stops. Assert it clears
    `current_run_id`, so the next claim mints a new one.
- [x] 5.7 Confirm the rest of the run-token lifecycle is already covered. A failed completion
  keeps `current_run_id`, and a config-change revival clears it. Add only the assertion that
  is missing.

- [x] 5.8 Extend the XML doc on `IScheduledCollectorRunner` in
  `src/Freeboard/Scheduler/IScheduledCollectorRunner.cs`. It says only that an implementation
  must honor the cancellation token. State that a runner which has NOT finished its work
  throws rather than returns. Give the reason. The service reads a normal return as a
  completed cycle and clears the run token. A runner that returns normally under a cancelled
  token therefore ends its cycle, and its next dispatch runs under a new run id. This is the
  contract 5.1 and 5.2 depend on, so it belongs on the interface a runner is written against.

## 6. Verification

- [x] 6.1 `dotnet build`.
- [x] 6.2 `dotnet test` with no external services. Core, CLI, and web tests pass, and the
  gated suites skip cleanly. The scheduler orchestration tests run here, so this run proves
  the scheduler completion behavior.
- [x] 6.3 Bring up the test MySQL with
  `docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d`. Export
  `FREEBOARD_TEST_DB`. Run `dotnet test tests/Freeboard.Persistence.Tests`. Migration `024`,
  both idempotency keys, the collation cases, and the cycle-wide derivation then actually
  execute.
- [x] 6.4 Re-run `dotnet test tests/Freeboard.Persistence.Tests` against an already-migrated
  database. Confirm every evidence test still passes. The migration is additive to DATA. No
  recorded run changes its values or its derived status. It is not additive to DDL freedom,
  so `Migration015AddsNullableColumnsWithoutBackfill` needs the 1.6 rewrite to pass at all.
  Confirm that test passes for the reason 1.6 gives, and not by weakening what it asserts.
- [x] 6.5 `openspec validate "add-evidence-machine-and-error-result" --strict`.
