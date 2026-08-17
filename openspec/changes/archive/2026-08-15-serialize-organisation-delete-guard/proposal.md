## Why

`MySqlComplianceWriteStore.DeleteOrganisationAsync` refuses to delete an organisation that
still has a child organisation or a scope bound to it. It decides this with two plain
`SELECT COUNT(*)` reads inside its transaction. Neither read takes a lock, and neither
`assets.parent` nor `scopes.subject_id` carries a foreign key, so the database does not
serialize the guard against a concurrent write. A scope upsert can read the organisation as
live, insert a scope naming it as subject after the count has run, and commit. The delete
then removes the organisation and leaves a scope whose subject no longer resolves. The same
window exists for a concurrent child-organisation write.

The spec states the guard as an unqualified rule: the write is rejected "rather than leaving
an orphaned scope subject". The code cannot keep that promise under concurrency. Either the
code becomes as strong as the text, or the text becomes as weak as the code. This change
takes the first option, because the guard's whole purpose is to make an authoring error a
write-time refusal rather than a state to reconcile later.

## What Changes

- `DeleteOrganisationAsync` opens with a locking read of the organisation's own `assets` row
  (`SELECT ... FOR UPDATE`), before either count. The lock serializes the delete against any
  writer that reads the same row under a lock. Taking it first also means the transaction's
  later plain reads see a state that already includes anything that lock waited for.
- `OrganisationExistsAsync` becomes a locking read (`FOR SHARE`) of the same row. Its two
  callers are exactly the two writers that can create a reference to an organisation: the
  parent-exists check in `UpsertOrganisationAsync` and the subject-exists check in
  `UpsertScopeTargetAsync`. Both need the lock, so the helper changes unconditionally and
  gains no parameter. Its `COUNT(*)` shape is kept: an aggregate locking read still takes the
  row locks, so the edit is the `FOR SHARE` suffix and nothing else.
- The write paths that now block on a lock map a deadlock or a lock-wait timeout to a
  conflict result, so a retryable outcome reaches the caller as HTTP 409 rather than as a
  store failure. Unmapped, the four compliance paths reach
  `ComplianceEndpoints.IsStoreFailure`, which matches `DbException`, and are answered as an
  unreachable store - which is untrue and points the caller away from retrying.
- `MySqlAuthzAdministrationStore.AssignOrganisationRoleAsync` gets two mappings. Its insert
  carries a foreign key to the organisation's asset row, so it too now blocks on the delete's
  lock, and it catches only a duplicate key today. Its endpoint has no exception handling and
  the app registers no exception-handling middleware, so anything unmapped there answers a bare
  HTTP 500. A lock failure becomes a conflict, as on the compliance paths. A foreign-key failure
  becomes an invalid result: when the delete COMMITS while the assignment waits - the ordinary
  production shape, since the delete is five short statements - the assignment resumes and fails the
  foreign key rather than timing out, and the parent row is gone for good, so a retryable answer
  would be wrong. Both statuses already exist on the result type and are already answered by the
  endpoint, so the change is a widened catch filter.
- `InstrumentedConnectionFactory` bounds how long it waits for an interleaved action. Its
  current contract awaits the action to completion, which cannot express a test where the
  racing writer is meant to block. The bound turns a 50-second lock-wait hang into a fast
  failure that names the boundary it happened at.
- New MySQL integration coverage races an organisation delete against a scope upsert on the
  same subject, and against a child-organisation create, at every statement boundary of the
  delete. The first boundary AFTER the locking read is asserted by name: it is the interleaving
  that a plain read ahead of the locking read gets wrong, so it is the regression guard for the
  statement order the fix depends on. The same file covers the role assignment on both sides of
  the race, so the store's two new mappings are executed rather than only read.
- The `compliance-write` spec states the guard's strength: what the delete is serialized
  against, and what remains outside that promise.

MIT, not an EE carve-out. Every changed file is in `src/Freeboard.Persistence`,
`src/Freeboard`, or `tests/`. Nothing touches `src/Freeboard.Enterprise`, and no MIT project
gains a reference to it.

### Non-goals

- **Serializing the GitOps importer's own writes.** `MySqlGitOpsImporter` runs in the CLI, a
  different process. It is not changed. The design shows why the asset-row lock already
  covers the case that matters: the importer writes the declared asset rows before it
  replaces the scopes, and that upsert takes an exclusive lock the delete's own lock waits on,
  so an app delete of a config-declared organisation cannot commit between those two steps. A
  scope naming an organisation the config does not declare is dangling by construction - the
  import itself prunes that organisation - and is already reported by the importer's own
  unresolved-subject warning. Adding locks to the importer was considered and rejected: it
  would not change any outcome, because the importer tolerates a dangling subject by contract
  and would insert the same row after waiting. The delete's lock order does make a DEADLOCK
  between the app delete and a concurrent import possible, for an organisation the config does
  not declare. That is accepted rather than mapped: the aborted side writes nothing and leaves no
  orphan, the app side of it is already mapped to a retryable conflict, and the CLI already
  answers a database failure with exit 3 and the server's message. The design gives the reasoning
  and the rejected alternatives.
- **Issue #131.** The declared/discovered collision guard in
  `MySqlGitOpsImporter.GuardDeclaredDiscoveredCollisionAsync` cannot share a mechanism with
  this change and must not be folded into it. It needs gap locks over primary-key values that
  do not exist yet, and its racing writer is `MySqlAssetWriteStore`, which runs at
  `ReadCommitted` explicitly so that it takes no gap locks, with a duplicate-key retry
  instead. A gap-lock fix there fights a documented decision. The two issues are separate.
- **The unconditional success result on delete.** `DeleteOrganisationAsync` returns
  `WriteResult.Success` without checking rows affected, so deleting an id that names no
  organisation, or an asset of the wrong type, reports success. This is a real defect found
  while investigating, it is not a concurrency defect, and fixing it here would change the
  delete's observable result for reasons this change has nothing to say about. It needs its own
  issue, and this change opens that issue as #152 rather than only naming it, because two
  accepted risks in the design lean on the fix landing.
- **A general locking or retry framework.** The change adds no lock manager, no advisory-lock
  namespace, and no retry loop. It makes two existing reads locking reads.
- **Vendor and machine subjects.** Only `Company` and `Department` assets are deletable
  through this path, so only organisation subjects are in scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `compliance-write`: the requirement "App-managed writes for organisations and scope
  dispositions" states the organisation delete guard as an unqualified rule. It gains the
  concurrency contract: which concurrent writers the guard is serialized against, that the
  losing writer is refused rather than reordered, and what stays outside the promise.

## Impact

- `src/Freeboard.Persistence/MySqlComplianceWriteStore.cs`: one added locking read in
  `DeleteOrganisationAsync`, one existing read in `OrganisationExistsAsync` made locking, and
  a lock-failure to conflict mapping on the four affected write paths.
- `src/Freeboard.Persistence/MySqlAuthzAdministrationStore.cs`: the lock-failure and
  foreign-key mappings added to the existing catch on `AssignOrganisationRoleAsync`.
- `tests/Freeboard.TestInfrastructure/InstrumentedConnectionFactory.cs`: a bounded wait on the
  interleaved action, and the contract comment that describes it.
- `tests/Freeboard.TestInfrastructure/`: a small connection-factory decorator that sets a low
  `innodb_lock_wait_timeout` on each connection it opens, so the racing writer in the new test
  is refused by the SERVER with the same lock-wait timeout the store's mapping must handle,
  rather than hanging or failing as a client-side command timeout. The server-global setting is
  shared by the parallel integration run, so a decorator is the smallest way to bound one
  session.
- `tests/Freeboard.Persistence.Tests/`: a new MySQL integration test file for the raced
  delete. It skips cleanly when `FREEBOARD_TEST_DB` is unset, like every other integration
  test.
- `tests/Freeboard.Web.Tests/`: CONDITIONALLY, a test that the delete endpoint answers 409 for a
  conflict result from the store. Nothing is added here if the existing endpoint tests already
  cover that mapping for this route.
- No schema migration. The change uses the indexes the tables already carry.
- No change to the HTTP surface. The endpoints already map a conflict result to 409.
- Operational: an organisation write and a scope write now hold a shared lock on one asset row
  until they commit. Two writes under the same organisation do not block each other. An
  organisation delete does block them, and is blocked by them, which is the point.
