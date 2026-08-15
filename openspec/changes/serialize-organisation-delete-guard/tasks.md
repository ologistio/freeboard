## 1. Make a blocking racing writer expressible in the interleave hook (`test(infra)`)

This group is a HARD PREREQUISITE for group 4. Today `InstrumentedConnectionFactory` awaits an
armed action to completion, so a racing writer that blocks on a lock hangs the test until
`innodb_lock_wait_timeout` and then fails for the wrong reason.

- [x] 1.1 In `tests/Freeboard.TestInfrastructure/InstrumentedConnectionFactory.cs`, bound the
  await in `BeforeCommandAsync`. On expiry, throw with a message naming the command ordinal
  the hook was interleaving before and stating that the armed action did not settle. Pick a
  cap well above any current interleaved action's runtime; it is a diagnostic backstop, not a
  synchronization mechanism.
- [x] 1.2 Make the cap settable per arm, defaulted, so a test whose racing writer is meant to
  block can state its own bound alongside the session lock-wait timeout it sets. Do not add a
  second arming method for it.
- [x] 1.3 Update the class comment to state the new contract: an armed action that may block
  MUST bound its own wait (the racing session's `innodb_lock_wait_timeout`), and the factory's
  cap only turns a hang into a fast, correctly attributed failure. The current comment claims
  the action runs to completion, so leaving it would make it false. While rewriting it, say
  "uninstrumented" where it currently says "UNDECORATED": the lock-wait decorator from 1.4 makes
  "undecorated" ambiguous, and the rule the sentence states is about this factory, not that one.
  Record what the cap does NOT
  do, so a reader does not have to guess: on expiry the throw propagates out of the execute
  method, the `await using` on the transaction and the connection rolls the delete back and
  releases its locks, so the transaction is left in a defined state - but the abandoned action
  keeps running against a database the test fixture is about to drop, and nothing observes it.
  The rollback releases the very locks it was waiting for, so it MAY COMPLETE (committing a
  write into a fixture the test has stopped tracking) or MAY FAULT. Either way the cap is a
  diagnostic, not a cancellation: a test that needs the abandoned action's outcome must observe
  it after the rollback rather than assume it failed.
- [x] 1.4 Add a connection-factory decorator in `tests/Freeboard.TestInfrastructure/` that
  issues `SET SESSION innodb_lock_wait_timeout = <n>` on each connection it opens, so a store
  under test can be given a session that fails fast when it blocks. Keep it to that one job.
  Do NOT set the server global instead: the integration tests run in parallel against one
  server, so a global would change every other test's lock-wait behaviour.
- [x] 1.5 Run `dotnet test tests/Freeboard.Web.Tests` with `FREEBOARD_TEST_DB` set and confirm
  `ComplianceSnapshotConcurrencyTests` still passes unchanged. Those tests are the existing
  callers of the hook and must not become slower or flakier.

## 2. Serialize the organisation delete guard (`fix(persistence)`)

- [x] 2.1 In `src/Freeboard.Persistence/MySqlComplianceWriteStore.cs`, make
  `DeleteOrganisationAsync` open with a locking read of the organisation's own row:
  `SELECT id FROM assets WHERE id = @Id AND type IN ('Company', 'Department') FOR UPDATE`, as
  the FIRST statement in the transaction, before either count. Do not branch on its result;
  the unconditional-success defect is out of scope for this change.
- [x] 2.2 Make `OrganisationExistsAsync` a locking read by appending `FOR SHARE`. Change it
  unconditionally, with no new parameter: both callers (the parent-exists check in
  `UpsertOrganisationAsync`, the subject-exists check in `UpsertScopeTargetAsync`) are exactly
  the two writers that must be serialized against the delete. Keep the `COUNT(*)` shape; an
  aggregate locking read takes the row locks, so reshaping the query into a row-returning one
  buys nothing and enlarges the diff.
- [x] 2.3 Comment the WHY at both sites, in the terms of the invariant rather than the
  mechanism: the locking read must be FIRST in the delete so the transaction's later plain
  reads are taken after any wait, and the existence check must be a locking read so a writer
  that waited for the delete sees the row is gone instead of answering from a stale snapshot.
  Follow `comment-etiquette.md`: two short comments, no narration of the SQL.
- [x] 2.4 Update the class doc comment on `MySqlComplianceWriteStore`. It currently explains
  that the app guards hold referential wholeness where the database carries no foreign key; it
  should now also say that the organisation delete and the writes that reference an
  organisation serialize on the organisation's asset row.
- [x] 2.5 Do NOT pass an isolation level to `BeginTransactionAsync` on any touched path. The
  guarantee holds at the server default and at every level above it, so an explicit level here
  would read as load-bearing when nothing depends on it.

## 3. Map lock failures to a retryable conflict (`fix(persistence)`)

- [x] 3.1 In the same file, catch `MySqlException` with `MySqlErrorCode.LockDeadlock` and
  `MySqlErrorCode.LockWaitTimeout` on `DeleteOrganisationAsync`, `UpsertOrganisationAsync`,
  `UpsertScopeTargetAsync`, and `DeleteScopeTargetAsync`, and return `WriteResult.Conflict`.

  Keep the deadlock code on the DELETE path even though nothing in group 4 races it there. It is
  reachable: a concurrent GitOps import takes the organisation's assignment rows before its asset
  row, the delete takes them the other way round, and the two deadlock. The delete is the likelier
  victim of the pair, so this mapping is what turns that into a retryable 409.

  The catch must span the WHOLE transaction body, from the locking read onward. Do NOT put it
  on the existing duplicate-key `try`. That try wraps only the INSERT, and every point that
  newly blocks sits EARLIER and outside it: `LockedParentChangedAsync`'s `FOR UPDATE`, the
  scope row's `FOR UPDATE`, and both `OrganisationExistsAsync` call sites.
  `DeleteOrganisationAsync` has no `try` at all today, so it gets one around its whole body. A
  catch placed around the INSERT instead is a NO-OP for this change: a lock-wait timeout on the
  locking read escapes it, reaches `ComplianceEndpoints.IsStoreFailure`, which matches
  `DbException`, and is answered as an unreachable store - the exact defect this group exists
  to remove. Leave each duplicate-key catch exactly as it is, as an inner catch on its own
  INSERT. Do not merge the two filters.

  This is deliberately NOT the shape 3.3 describes, and the two must not be conflated. On the
  authz path the only newly blocking statement IS the INSERT the existing try already wraps, so
  there a widened `when` clause on that same try is correct and sufficient. Here it is not.

  Match the message style of the conflicts the file already returns. Do not invent a longer one
  to carry retry advice: `ComplianceWriteEndpoints.Conflict()` builds a fixed problem body and
  never reads `result.Error` on the conflict branch, so any such text is dead on the wire.
  Four call sites, so factor the filter into one private helper rather than repeating it. Add
  no retry loop.
- [x] 3.2 Confirm by reading `src/Freeboard/Compliance/ComplianceWriteEndpoints.cs` that
  `RunAsync` maps a conflict result to 409 and that nothing else needs to change on the
  endpoint. Do not add a new mapping there.
- [x] 3.3 In `src/Freeboard.Persistence/MySqlAuthzAdministrationStore.cs`, widen the existing
  catch on `AssignOrganisationRoleAsync` to TWO mappings, not one. Its insert carries a foreign
  key to the organisation's asset row, so the delete's new lock makes it block where it never
  did; today it catches only `MySqlErrorCode.DuplicateKeyEntry`, its endpoint has no exception
  handling, and the app registers no exception-handling middleware, so anything else answers a
  bare 500. Map:
  - `MySqlErrorCode.LockDeadlock` and `MySqlErrorCode.LockWaitTimeout` to
    `AuthzWriteResult.Conflict`. Unlike the compliance route, this endpoint DOES surface the
    message, so write one that says the write lost a race and can be retried.
  - `MySqlErrorCode.NoReferencedRow` and `MySqlErrorCode.NoReferencedRow2` (1216/1452) to
    `AuthzWriteResult.Invalid`. This is the case PRODUCTION produces, and mapping only the lock
    codes would leave the 500 in place while looking fixed: the method's organisation-exists
    check at the top is a plain consistent read, so a blocked insert that resumes after the
    delete COMMITS fails the foreign key rather than timing out. A conflict would be the wrong
    answer here - the parent row is gone for good, so a retry cannot win - and `Invalid` is
    already what the method returns when it observes the same condition in time. Word the
    message for the reference generally (the organisation, the user, or the role): the insert
    has three foreign-key parents and all three checks ahead of it are plain reads, so do not
    assert which one vanished.

  Do not restructure the method or add a transaction; this is a widened `when` clause plus one
  more catch on the same try.
- [x] 3.4 Confirm by reading `src/Freeboard/Authz/RoleAssignmentEndpoints.cs` that `MapAssign`
  already answers a conflict status with 409 and an invalid status with 422, both carrying the
  result's message, so no endpoint change is needed. This is why 3.3 stays a catch filter rather
  than a new result path.
- [x] 3.5 Add a web test asserting the delete endpoint answers 409 for a conflict result from
  the store, if the existing tests do not already cover that mapping for this route. Skip this
  task if they do; do not add a duplicate.

## 4. Prove the serialization against a real MySQL (`test(persistence)`)

- [x] 4.1 Add a new integration test file under `tests/Freeboard.Persistence.Tests/`, gated on
  `FREEBOARD_TEST_DB` through `MySqlTestDatabase.TryCreateAsync` plus `Skip.If`, and marked
  `[Trait("Category", TestCategories.Integration)]`, matching
  `AssetUnificationIntegrationTests`. Seed one organisation, one standard, one user, and nothing
  that already references the organisation. The user row is for the role-assignment cases in 4.9.
  `AssignOrganisationRoleAsync` checks the user exists before it reaches the INSERT, so without a
  seeded user both cases stop at that check with "User '...' does not exist." and never reach the
  foreign-key path they exist to prove. `AssetUnificationIntegrationTests` already has the
  `INSERT INTO users` column list to copy. Seed no role: migration 010 seeds three
  organisation-scoped roles (`org-owner`, `compliance-manager`, `compliance-reader`), so 4.9 uses
  one of those and must not insert a duplicate.
- [x] 4.2 Write one helper that restores the fixture to the pre-race state - the organisation
  present, no referencing scope, no child - and call it before EVERY run in this group,
  including the measuring run in 4.3. Both the unit under test and the racing writer are
  destructive here (the measuring run deletes the organisation; a writer-wins run leaves a
  committed scope; a delete-wins run leaves the organisation gone), so without a per-run reset
  every run after the first races nothing and 4.5's "both outcomes occur" assertion can pass by
  fixture exhaustion rather than by the race. The template in `ComplianceSnapshotConcurrencyTests`
  already resets before every run; the only difference here is that the reset must ALSO undo the
  unit under test's own mutation, because there only the racing writer mutates. Copy one detail
  from it exactly: the reset runs BEFORE `Arm`. `Arm` zeroes the command counter, so a reset
  issued after arming through the instrumented factory counts as commands and shifts every
  boundary. Reset before arming, or through the UNINSTRUMENTED factory. Say "uninstrumented",
  not "undecorated": the lock-wait decorator from 1.4 is also a decorator, and the two rules are
  orthogonal.
- [x] 4.3 Measure the delete's statement count by arming `InstrumentedConnectionFactory` with
  no action and running one delete, as `ComplianceSnapshotConcurrencyTests` does. Do not
  hard-code the count: a later change to the delete must not silently stop racing a boundary.
- [x] 4.4 Race a scope upsert naming the organisation as `subject` at every statement boundary
  of the delete. The racing action opens its connections from the UNINSTRUMENTED factory,
  wrapped in the lock-wait decorator from 1.4, so a blocked write fails fast as a conflict
  instead of hanging. After each run assert the run actually REACHED its boundary, the way the template
  does: a run whose delete issued fewer statements than the boundary raced nothing and proves
  nothing.
- [x] 4.5 Assert, at every boundary: the delete and the scope upsert are never both successful;
  the LOSING side returned a rejection or conflict RESULT rather than throwing (a
  `MySqlException` escaping to the test means the lock failure was not mapped, which is the
  defect group 3 exists to prevent, and this assertion is the coverage for the spec's
  retryable-conflict scenario); and afterwards no `scopes` row names a `subject_id` with no
  matching `assets` row. Assert across the boundaries that BOTH outcomes occur at least once,
  so the test cannot pass on a fixture where the race never happens.
- [x] 4.6 Assert the first boundary AFTER the locking read BY NAME, as its own case, not only
  as a member of the sweep. Name it as an ORDINAL - boundary 2, because 2.1 makes the locking
  read the delete's FIRST statement - since the test counts commands and cannot introspect which
  one is the locking read. That one number does not conflict with 4.3: the sweep's upper bound
  stays measured, so a delete that grows a statement still races every boundary it has. There
  the delete already holds `FOR UPDATE`, so the racing write
  must be refused and the delete must succeed leaving no orphan. This is the regression guard
  for 2.1's statement order: move a plain read in front of the lock and this boundary finds the
  delete holding nothing, so the racing write commits, the later count answers from a read view
  that predates it, and BOTH succeed. Say that in the assertion message. Do NOT use boundary 1
  for this - with no command yet issued the delete has taken no read view, so even the unfixed
  store sees the racing commit and rejects, and a test asserting boundary 1 proves nothing
  about statement order. Boundary 1 stays in the sweep as the ordinary writer-wins case.
- [x] 4.7 Repeat 4.4 to 4.6 AND 4.8 with the racing write being an organisation create naming
  the organisation under delete as `parent`, asserting afterwards that no `assets` row names a
  `parent` with no matching row. Give the child half its own full sweep rather than a spot
  check: the two halves fail independently, and only the scope half has a compensating warning
  today. 4.8 is included on purpose, so the child half gets the non-raced pair too. Its
  reference-then-delete direction overlaps existing coverage in
  `AssetUnificationIntegrationTests`, but its delete-then-reference direction - creating a child
  under an organisation a committed delete removed - is covered nowhere today, and it is the
  tail the raced sweep cannot reach.
- [x] 4.8 Add the two non-raced orderings as their own cases. Write the reference to
  completion, then delete, and assert the delete is rejected (this pins that the locking read
  did not weaken the plain guard). Then delete to completion, then write the reference, and
  assert the write is rejected because the organisation does not resolve. The second case
  covers the tail the raced test cannot reach: in the raced test the blocked writer times out
  rather than waiting for the delete to commit, so it never resumes to observe the row gone.
- [x] 4.9 Cover BOTH failures group 3 maps on `AssignOrganisationRoleAsync`, in the same file
  and off the same fixture. Nothing else in this plan executes that catch filter: 3.4 only reads
  the endpoint, and reading it proves the mapping exists, not that the filter matches the error
  the server actually sends.
  - Lock-wait timeout: race an assignment against the delete the way 4.4 races a scope write -
    arm at boundary 2 so the delete holds `FOR UPDATE`, with the assign wrapped in the lock-wait
    decorator from 1.4 - and assert it returns a CONFLICT result while the delete succeeds.
  - Foreign-key failure: this one cannot be reached that way, because the racing session always
    times out before the delete commits. INVERT the instrumentation instead: arm the hook on the
    ASSIGN's connection factory, with an armed action that runs the whole delete to completion.
    Arm at ORDINAL 4, or measure the assign's statement count the way 4.3 measures the delete's
    and arm at the command after its third read. The hook takes a number, not a description, so
    naming the point as prose the way 4.6 refuses to would leave the ordinal to guesswork.
    `AssignOrganisationRoleAsync` issues exactly three plain reads before its INSERT - the role
    scope, the user exists, the organisation exists - and transactions are not counted, so the
    INSERT is command 4.

    It must be the command after the THIRD read, not an earlier one, and the reason is that an
    earlier arm is GREEN EITHER WAY while only sometimes executing the mapping. At
    `REPEATABLE READ` an arm at 2 or 3 does end in 1452, because the assign's read view is
    pinned at its first read and its organisation-exists check still answers 1. At
    `READ COMMITTED` an arm at 3 makes that check itself see the row gone, so the method returns
    `Invalid` from its own existence guard and never reaches the INSERT. The assertion still
    passes, on a run that executed nothing group 3 added. Arm at 4 and the assign holds no locks
    at the arm point, `BeforeCommandAsync` awaits the delete to completion, the delete runs
    unobstructed and commits, and the INSERT then fails the foreign key at both levels.

    The assign's INSERT runs against a committed delete, its foreign key is re-checked as a
    current read, and the store must return the INVALID result. This is the case production
    produces, so it is the one that matters most. A `MySqlException` escaping to the test is the
    bare 500 group 3 exists to remove. No barrier, no sleep, and no lock-wait decorator is
    needed: the armed delete is meant to RUN TO COMPLETION, not to block, so it opens from the
    UNINSTRUMENTED factory and is wrapped in nothing. Do not carry the decorator over from the
    lock-wait case above.
- [x] 4.10 Write the file's class comment in the style of `ComplianceSnapshotConcurrencyTests`:
  what is raced, why the assertion is the PAIR of outcomes rather than which one won, why every
  run re-seeds the fixture, why the racing session bounds its own lock wait, which boundary is
  the statement-order guard and why it is not boundary 1, and why the role-assignment cases
  instrument opposite sides of the race.

## 5. Verification and follow-up

- [x] 5.1 `dotnet build` with no warnings.
- [x] 5.2 `dotnet format --verify-no-changes`. CI's lint gate runs it, so a build-and-test pass
  alone does not mean CI is green.
- [x] 5.3 `dotnet test` with `FREEBOARD_TEST_DB` UNSET: everything passes and the new tests
  skip cleanly.
- [x] 5.4 `dotnet test` with `FREEBOARD_TEST_DB` set against the compose MySQL: the new tests
  run and pass, and no existing integration test regresses or slows noticeably.
- [x] 5.5 Confirm the new test FAILS against the unmodified store. Revert group 2 locally, run
  it, see the never-both-successful assertion break, and restore. A concurrency test that
  passes both ways proves nothing.
- [x] 5.6 Confirm 4.6 is a real statement-order guard. With group 2 in place, move the child
  count AHEAD of the locking read, run the test, and see the named boundary case go RED: the
  racing scope write and the delete both succeed and an orphaned scope is left behind. Restore
  afterwards. Check the failure is that one, not an incidental error; if the case still passes,
  it is not pinning what it claims to. Note the dependency before concluding anything from a
  green run: the stale-read half of this defect needs `REPEATABLE READ`, the server default the
  store inherits and what the test compose file runs. Against a server configured
  `READ COMMITTED` the moved count takes a fresh snapshot, sees the racing scope, and the check
  cannot go red - that would mean the environment is wrong for this step, not that the guard
  holds.
- [x] 5.7 `npx markdownlint-cli2 "**/*.md"`.
- [ ] 5.8 Open a GitHub issue for the rows-affected check on `DeleteOrganisationAsync`, then put
  its number in the two design risk bullets that lean on it (the absent-id gap-lock bullet and
  the wrong-type record-lock bullet). Both accept their blast radius partly because that check
  closes it, and nothing tracks it today, so the dependency would be lost.

  State in the issue WHICH form is meant. The delete must RETURN EARLY when its locking read
  matches no `Company`/`Department` row, so the transaction ends before it holds the lock for the
  rest of its body. A rows-affected check on the final `DELETE FROM assets` answers not-found
  correctly and still holds the gap or record lock for the whole transaction: it closes the
  unconditional-success defect and leaves the lock window those two bullets accept. Say both
  halves the early return closes - the gap lock an absent id takes, and the exclusive record lock
  a wrong-type id takes on a live vendor or machine row - so a later reader can tell what the
  issue is for.
