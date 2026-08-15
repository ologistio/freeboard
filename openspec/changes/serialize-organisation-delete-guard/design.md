## Context

`MySqlComplianceWriteStore.DeleteOrganisationAsync` runs one transaction on its own
connection at the server's default isolation. It counts child organisations, counts
referencing scopes, prunes the organisation's role assignments, deletes the asset row, and
commits. Both counts are plain `SELECT COUNT(*)` statements. A plain `SELECT` in InnoDB is a
consistent read: it takes no lock and answers from the transaction's read view. So the guard
observes a state and then acts on it, with nothing stopping another transaction from changing
that state in between.

Four writers can create a reference to an organisation:

| Writer | Reference it creates | Process |
| --- | --- | --- |
| `MySqlComplianceWriteStore.UpsertScopeTargetAsync` | a `scopes` row whose `subject_id` is the organisation | web app |
| `MySqlComplianceWriteStore.UpsertOrganisationAsync` | an `assets` row whose `parent` is the organisation | web app |
| `MySqlAuthzAdministrationStore.AssignOrganisationRoleAsync` | an `authz_organisation_role_assignments` row whose `organisation_id` is the organisation | web app |
| `MySqlGitOpsImporter` | either of the first two, through `UpsertAssetsAsync` and `ReplaceScopesAsync` | CLI |

The role assignment is the odd one out, and the delete treats it differently. Its
`organisation_id` DOES carry a foreign key - `fk_authz_org_role_assignments_org ... REFERENCES
assets (id) ON DELETE RESTRICT` - so the delete does not refuse over it. It PRUNES the
assignments first and then deletes the asset. That makes it a writer this design still has to
account for, because the new lock changes its behaviour: see Decision 3.

`MySqlAssetWriteStore` is not on that list. It writes only discovered rows of `type` =
`Machine` (its single `INSERT INTO assets` writes the constant `MachineType`), and a `Machine`
is not a child organisation, so it cannot make the child half of the guard wrong. This also
means no discovered `Company`/`Department` row exists, which matters for the importer argument
in Decision 4.

The two writers the GUARD counts share one seam. Both check their referenced organisation
through `MySqlComplianceWriteStore.OrganisationExistsAsync`, and nothing else in either method
reads that row. That single shared read is what makes this fixable cheaply, and it corrects the
framing in the issue: the scope path does read the asset row already, so the work is to make an
existing read locking, not to add a new one. `MySqlAuthzAdministrationStore` has a same-named
private helper of its own; this change does not touch it, and the two must not be confused.

Locking is not a new mechanism in this file. `FOR UPDATE` already appears twice in it, and in
eight files across the persistence project. `DeleteOrganisationAsync` is the outlier that
guards without one. `FOR SHARE`, `GET_LOCK`, `LOCK TABLES`, and an explicit `SERIALIZABLE` do
not appear anywhere in `src/`.

The schema facts the design depends on: `scopes` carries `KEY ix_scopes_subject_id
(subject_id)` and no foreign key on `subject_id`; `assets` carries `PRIMARY KEY (id)` and
`UNIQUE KEY uq_assets_parent_identity (parent, identity_kind, identity_value)` and no foreign
key on `parent`. Migration 020 documents the missing subject foreign key as deliberate: a
subject is dangling-tolerated.

### Behaviour confirmed against the server

The claims below are load-bearing and were checked against MySQL 8.4, the version the test
compose file runs, at its default `REPEATABLE-READ` unless another level is named. They are
recorded here because each one, if false, changes the design.

The interleaving points are named by the hook's own numbering: the hook runs the racing writer
immediately BEFORE the Nth command the delete issues, so "boundary N" means the racing writer
commits after the delete's transaction has begun and after its first N-1 commands, but before
its Nth.

1. `SELECT COUNT(*) ... FOR SHARE` is accepted AND takes the lock. A session holding
   `FOR UPDATE` on the row makes it wait to the lock timeout. So `OrganisationExistsAsync`
   needs only the `FOR SHARE` suffix; it does NOT need rewriting into a row-returning query.
2. `INSERT ... ON DUPLICATE KEY UPDATE` on an existing row and `SELECT ... FOR UPDATE` on that
   row block each other in BOTH directions. This is what makes the importer's own statement
   order sufficient (Decision 4).
3. Boundary 1 does NOT distinguish the fixed store from the unfixed one, and the guard must not
   be described as though it does. A transaction's read view is created at its first CONSISTENT
   read, not at `BEGIN` (property 2 of Decision 1), and at boundary 1 the delete has issued no
   command yet. So in the UNFIXED store the plain child count is itself the first consistent
   read and runs after the racing writer committed: the scope count answers 1 and the unfixed
   store already rejects. The fixed store rejects there too, by the same reading. Boundary 1 is
   the writer-wins case, and nothing more.
4. The interleaving that separates them is the first boundary AFTER the locking read. In the
   UNFIXED store the delete has already taken its read view at the child count, so the racing
   writer commits unblocked and the later scope count answers 0 from a read view that predates
   that commit: the delete proceeds and orphans the scope. Both writes report success. Under
   the fix the delete already holds `FOR UPDATE` at that point, the racing writer blocks on its
   `FOR SHARE` and fails with `ER_LOCK_WAIT_TIMEOUT` (1205), and the delete completes with no
   orphan. This is the regression guard (Decision 7), not boundary 1.
5. Claim 4's STALE-READ half is specific to `REPEATABLE-READ`. Replayed at `READ-COMMITTED`,
   that same interleaving makes the scope count answer 1, because each consistent read there
   takes a fresh snapshot, so the unfixed store rejects and the orphan does not appear. This
   does NOT make the unfixed guard sound at `READ-COMMITTED`. It has a second window, and that
   one is level-independent: a racing writer that commits AFTER both counts have returned,
   before the delete removes the asset row. No read view is involved there, because both counts
   have already answered. Replayed at `READ-COMMITTED` with the unfixed statement order and the
   scope writer committing between the scope count and `DELETE FROM assets`, the delete saw
   `child_count = 0` and `scope_count = 0`, the writer's plain existence read answered
   `org_exists = 1`, both committed, and one scope was left naming a subject the delete had
   removed. So the DEFECT is present at every level; only its boundary-2 manifestation needs
   `REPEATABLE-READ`.
6. Every property the FIX depends on holds at both levels, because record lock compatibility
   and locking reads being current reads do not vary with isolation. The same
   `READ-COMMITTED` replay with the fix applied had the scope writer's `FOR SHARE` block on the
   delete's exclusive lock and then current-read `org_exists = 0`, so the guard held. The design
   therefore does not pin a level (Decision 5).
7. Once the delete holds `FOR UPDATE` on the asset row, a concurrent
   `AssignOrganisationRoleAsync` INSERT blocks: InnoDB takes a shared record lock on the parent
   `assets` row to check the foreign key, and that lock is incompatible with the delete's
   exclusive one. WHICH error ends the wait depends on which side gives up first, and the two
   are not interchangeable. If the assign's own lock wait expires first, it fails with 1205. If
   the delete COMMITS first, the assign resumes, re-checks the foreign key as a current read,
   finds no parent row, and fails with 1452 (`ER_NO_REFERENCED_ROW_2`, SQLSTATE 23000). The
   probe confirmed the second shape: the assign's plain organisation-exists read answered
   `org_exists = 1`, its INSERT blocked, and on release it returned `ERROR 1452 (23000)`.
   The 1452 shape is the one production produces, because the delete is five short statements
   and commits long before any ordinary lock wait expires. The 1205 shape needs the delete to be
   held open, which is the test's arrangement, not the app's.
8. That blocked INSERT holds NO record lock on `authz_organisation_role_assignments` while it
   waits. `performance_schema.data_locks` shows it holding only a table-level `IX` there, plus
   granted shared record locks on the `users` and `authz_roles` parents, while waiting for
   `S,REC_NOT_GAP` on the `assets` row. InnoDB takes the foreign-key parent locks BEFORE it
   inserts the record, so this writer cannot close a lock cycle with the delete's assignment
   prune (Decision 3).
9. Two organisation upserts that each name the other's row as parent DO deadlock, reported as
   `ER_LOCK_DEADLOCK` (1213). Each holds an exclusive lock on its own row and then requests
   `FOR SHARE` on the other's. This is the cycle Decision 3's mapping exists for.
10. A `FOR UPDATE` that RETURNS NO ROWS still locks, and WHICH lock it takes depends on why it
    returned nothing. The two cases are not interchangeable and the Risks section judges them
    separately.
    - **No row with that id exists.** It takes a GAP lock: `data_locks` reports `X,GAP` on the
      primary key at the adjacent existing id, and a concurrent INSERT of an id inside that gap
      blocks and then fails with 1205, while an UPDATE of an existing neighbouring row is
      unaffected. It blocks whatever level the INSERTER runs at, `READ COMMITTED` included,
      because the gap lock belongs to the holder and an insert-intention lock conflicts with it.
      The blast radius is the gap between neighbouring ids.
    - **The row exists but the `type IN (...)` filter excludes it.** It takes an ordinary
      EXCLUSIVE RECORD lock, `X,REC_NOT_GAP`, on that live row. The primary-key equality search
      finds and locks the record, and the filter is applied after locking. At `REPEATABLE READ`
      the lock is not released. Probed against rows `('a-company','Company'),
      ('m-vendor','Vendor'), ('z-company','Company')` with one session holding the locking read
      on `'m-vendor'`: a concurrent `FOR SHARE` on that id gave 1205, a concurrent `UPDATE` of
      it gave 1205, an insert into the adjacent gap SUCCEEDED, and an insert into a child table
      whose foreign key names that id gave 1205. The blast radius is one live row belonging to
      another asset kind, plus everything that needs a lock on it.
11. The FIXED delete and the GitOps importer DEADLOCK, and the current delete does not. One
    session held `FOR UPDATE` on an organisation's asset row and then ran the delete's assignment
    prune; a second session had already run the importer's absent-organisation assignment prune
    (`... WHERE organisation_id NOT IN (...)`) and then ran its absent-declared-asset prune
    (`DELETE FROM assets WHERE source = 'declared' AND id NOT IN (...)`). InnoDB aborted the
    DELETE side with 1213, the importer committed, and the final state held no orphan. Replayed
    with the delete's CURRENT statement order - a plain count first, no locking read - the same
    interleaving committed BOTH sides with no deadlock, because both transactions then take the
    assignment rows before the asset row. InnoDB chose the delete as the victim, which matches its
    preference for the transaction that has changed fewer rows.

## Goals / Non-Goals

**Goals:**

- An organisation delete and a concurrent write that references that organisation NEVER both
  take effect. One of them may take effect and the other is then refused - the delete succeeds
  and the reference is refused, or the reference is written and the delete is refused - and
  when the store cannot order them, BOTH are refused. What is forbidden is the pair both
  taking effect, not a run in which neither does.
- The same guarantee for a concurrent scope insert and for a concurrent child-organisation
  write. The child half gets its own scenario and its own test case; it is not treated as a
  corollary of the scope half.
- A test that proves it against a real MySQL, driven by the existing counted-command hook
  rather than by sleeps.
- The `compliance-write` spec states the strength of the guard, so a future reader can tell
  what the contract promises without reading the SQL.

**Non-Goals:**

- Changing `MySqlGitOpsImporter`. See Decision 4.
- Issue #131. Its mechanism does not overlap; see the proposal's Non-goals.
- The unconditional `WriteResult.Success` on delete. Separate defect, separate issue.
- Any schema migration, new index, or new dependency.

## Decisions

### Decision 1: Serialize on the organisation's own asset row (issue option 1)

`DeleteOrganisationAsync` takes `SELECT id FROM assets WHERE id = @Id AND type IN ('Company',
'Department') FOR UPDATE` as its FIRST statement. `OrganisationExistsAsync` becomes the same
read with `FOR SHARE`. The exclusive lock and the shared lock are incompatible, so the delete
and any referencing write are strictly ordered on that one record, and both hold to commit.

Two properties of InnoDB make this sufficient, and both must hold:

1. A locking read is a CURRENT read. It reads the latest committed version, not the
   transaction's read view. So a referencing write that waits for the delete's exclusive lock
   sees, when it resumes, that the organisation is gone, and returns its existing
   "organisation does not exist" rejection. It does not insert a reference to a row it saw
   before the delete committed. A plain `SELECT COUNT(*)` would not give this: it would answer
   from a read view predating the delete, the writer would proceed, and the block would have
   only delayed the orphan rather than prevented it.
2. The read view of a transaction is created at its first CONSISTENT read, not at `BEGIN`. The
   delete's locking read is not a consistent read, so it creates no read view. When the delete
   instead waits for a referencing write's shared lock, its read view is created afterwards, by
   the child count. That count and the scope count therefore see the reference the other
   transaction just committed, and the guard rejects.

Trace both orders for a delete of `org-a` racing a scope upsert whose subject is `org-a`:

- Delete first. It holds the exclusive lock. The upsert blocks in
  `OrganisationExistsAsync`. The delete's counts see no scope, the row is deleted, commit.
  The upsert resumes, current-reads no `org-a` row, and returns "Organisation 'org-a' does not
  exist." One outcome.
- Upsert first. It holds the shared lock and inserts the scope. The delete blocks on its very
  first statement. The upsert commits. The delete acquires the exclusive lock, then takes its
  read view, then counts scopes and finds one, and rejects with "Cannot delete an organisation
  that still has scopes." One outcome.

The child-organisation race is the same argument with the other caller:
`UpsertOrganisationAsync` takes the shared lock on the PARENT row through the same helper.

Putting the locking read first is load-bearing, not stylistic, and it is not a theoretical
concern. The order fails in a specific window, and naming it precisely matters because the
obvious candidate is the wrong one. It is NOT the case that the racing writer commits before
the delete has run anything: there the delete has taken no read view yet, so even a plain count
sees the scope and refuses (confirmed claim 3). The window is the one where the delete has
already issued a plain read and so pinned a read view, and the racing writer then commits into
the gap before the scope count. With a plain count ahead of the lock, that count answers 0
against a scope that is committed and visible to every other session, both writes report
success, and the delete orphans the scope. The regression guard in Decision 7 pins exactly that
window.

`OrganisationExistsAsync` keeps its `COUNT(*)` shape and gains only the `FOR SHARE` suffix. An
aggregate locking read does take the row locks, so there is no reason to reshape the query.

The helper changes unconditionally rather than taking a parameter, because both of its callers
need the lock.

### Decision 2: Reject the three alternatives

**Option 2, a gap-locking read over `scopes`.** Attractive because a next-key lock on
`ix_scopes_subject_id` for a subject with no rows blocks any insert naming that subject, from
any process, with no change to any writer. It is nonetheless WRONG ON ITS OWN, for the reason
in Decision 1 property 1: the scope writer's subject-exists check is a consistent read, so a
writer whose insert blocks on the gap lock still believes the organisation is live, and simply
inserts once the delete commits. The gap lock reorders the insert without preventing it. To
close it, the scope writer's existence check has to become a current read anyway - which is
Decision 1 - and once it is, the gap lock adds nothing the row lock has not already done. It
also imports two costs for nothing: gap-lock behaviour depends on the transaction running at
REPEATABLE READ, so a deployment running `transaction_isolation = READ-COMMITTED` would lose
the guarantee with no diagnostic, and the child half would need a locking range read over
`uq_assets_parent_identity` whose index choice is an optimizer decision, not a guarantee.

**Option 3, an application-level advisory lock.** `GET_LOCK` appears nowhere in the codebase.
It would add a lock-name namespace, an acquire and release discipline on every path that
touches an organisation, and a new failure mode when a connection dies holding a lease. It
buys nothing the row lock does not, because the row it would stand in for is the row both
sides already read. New mechanism, larger surface, same outcome: rejected on
`code-as-liability.md`.

**Option 4, accept the race and document the guard as best-effort.** Cheapest to write, and
the issue reasonably reads it as consistent with a dangling-tolerated subject. It is rejected
on three grounds. First, the compensating control has a hole: the CLI sync warning fires only
when `gitops sync` runs, so an app-side delete that orphans a scope produces no operator
signal at all until someone next syncs, which may be never. Only the Statement of
Applicability page notice fires at read time. Second, the control covers the scope-subject
half only; the child-organisation half of the guard has no equivalent named notice, so
accepting the race there means accepting a silently orphaned child. Third, it is not actually
cheap: the acceptance criteria require the same spec delta and a test either way, so option 4
costs a spec delta plus a test that pins the defect, and it saves roughly two lines of SQL.
Paying two lines to keep a promise is better value than writing a paragraph to withdraw it.

### Decision 3: Map the new blocking failures to results, not exceptions

Introducing blocking introduces two new failure modes on these paths: `ER_LOCK_DEADLOCK`
(1213) and `ER_LOCK_WAIT_TIMEOUT` (1205). Both are retryable, and neither means the write was
invalid. Blocking also makes a THIRD failure far more reachable on the role-assignment path,
and that one is not retryable: see below.

A deadlock is newly reachable. `UpsertOrganisationAsync` already takes an exclusive lock on
its own row through `LockedParentChangedAsync`, and now takes a shared lock on its parent row.
Two concurrent upserts that each name the other's row as parent lock in opposite orders, and
this really does deadlock (confirmed claim 9).

The delete path is not cycle-free because it locks one row - it does not. Its assignment prune
takes exclusive record locks on every `authz_organisation_role_assignments` row for the
organisation, and its final statement takes one more on the asset row. Nor is it cycle-free
because all contention orders on a single record: other writers take assignment-row locks
without touching the asset row at all. The property a cycle needs to be false is narrower: NO
writer that holds a lock the delete needs goes on to REQUEST a lock the delete holds.

The checked set is every writer in `src/Freeboard.Persistence` that locks either of the two
things the delete locks - the organisation's `assets` row, or the organisation's
`authz_organisation_role_assignments` rows. A lock taken through a foreign-key parent check
counts, and so does a lock taken by a multi-row scan, because both hold record locks the delete
can wait on.

| Writer | Locks the asset row | Locks the assignment rows |
| --- | --- | --- |
| `MySqlComplianceWriteStore.UpsertOrganisationAsync` | yes: `FOR UPDATE` on its own row, `FOR SHARE` on the parent row | no |
| `MySqlComplianceWriteStore.UpsertScopeTargetAsync` | yes: its scope-row `FOR UPDATE` joins `assets`, plus `FOR SHARE` on the subject row | no |
| `MySqlComplianceWriteStore.DeleteScopeTargetAsync` | yes: its multi-table `DELETE` joins `assets`, which locks the joined row | no |
| `MySqlAssetWriteStore` | yes: `FOR UPDATE` on the identity key, then its `assets` update or insert | no |
| `MySqlAuthzAdministrationStore.AssignOrganisationRoleAsync` | yes: the shared foreign-key parent lock its INSERT takes | only after that parent lock is granted |
| `MySqlAuthzAdministrationStore.RevokeOrganisationRoleAsync` | no | yes: `FOR UPDATE` then `DELETE` |
| `MySqlAuthzAdministrationStore.DeleteCustomRoleAsync` | no | yes: through the `ON DELETE RESTRICT` child check on the role key |
| `MySqlGitOpsImporter` | yes: `UpsertAssetsAsync`, then the absent-declared-asset prune | yes: the absent-organisation assignment prune |

Nothing else reaches either. No writer deletes a `users` row, so the `ON DELETE CASCADE` from
`users` into the assignments table has no caller. `MySqlAuthzStore` only reads. The
system-role paths lock `authz_system_role_assignments`, a different table.

The four writers that lock the asset row and NOTHING the delete holds cannot be either half of a
cycle. `UpsertOrganisationAsync`, `UpsertScopeTargetAsync`, `DeleteScopeTargetAsync`, and
`MySqlAssetWriteStore` never name the assignments table, so the delete's prune never waits on
them, and their only contention with the delete is on the one record both want. The
upsert-against-upsert deadlock above is between two of them and does not involve the delete.

`AssignOrganisationRoleAsync` requests a lock the delete holds - the shared foreign-key lock on
the asset row - and holds none the delete needs while it waits. Its blocked INSERT holds no
record lock on the assignments table at all, because InnoDB takes the parent locks before
inserting the child record (confirmed claim 8). So the delete's prune never waits on it.

`RevokeOrganisationRoleAsync` is the opposite half, and the design has to name it because it
takes exactly the locks the delete's prune needs: `SELECT user_id FROM
authz_organisation_role_assignments WHERE role_key = @RoleKey AND organisation_id = @OrgId FOR
UPDATE`. It never locks the asset row, so the "ordered on one record" reading would be wrong
about it. It is still safe, because after that locking read it only DELETEs rows it already
holds and commits. It requests nothing the delete holds, so it can never be the waiting half of
a cycle. The delete may wait on it; it never waits on the delete.

`DeleteCustomRoleAsync` is the third authz writer, and it is easy to miss because no statement
in it names `authz_organisation_role_assignments`. Its `DELETE FROM authz_roles` makes InnoDB
run the `ON DELETE RESTRICT` child check on `fk_authz_org_role_assignments_role`, which locks the
assignment rows carrying that role key. It cannot close a cycle in either direction. At the
point where it could block on the delete's prune it holds only its `authz_roles` row, which the
delete never touches, so the delete cannot be waiting on it then. Once past the check it may
hold assignment rows the delete's prune needs, but it then requests only an `authz_audit_events`
insert and a commit, and the delete locks neither. It never locks the asset row at all. It is
out of scope for this change in every other way - like the rest of the authz store it maps
neither lock code - but the invariant has to survive it, and it does.

#### The importer breaks the property, and the cycle is real

`MySqlGitOpsImporter` is the one writer that locks BOTH things, and it takes them in the OPPOSITE
order to the fixed delete. It prunes absent organisation assignments (`DELETE FROM
authz_organisation_role_assignments WHERE organisation_id NOT IN @KeepIds`) and only afterwards
prunes absent declared assets (`DELETE FROM assets WHERE source = 'declared' AND id NOT IN
@KeepIds`). That order is required, not incidental: the assignment foreign key into `assets` is
`ON DELETE RESTRICT`, so a surviving assignment would wedge the asset prune, and the importer's
own comments record the prune order as what keeps those RESTRICT foreign keys satisfied. It
cannot be reordered to match the delete without breaking that.

So the property fails here, and the cycle it allows is ABBA. The delete holds the organisation's
asset row from its first statement and then waits for the assignment rows the importer's prune
holds. The importer holds those assignment rows and then asks for the asset row the delete holds.
Both importer prunes are `NOT IN` scans, so the assignment rows it locks are wide rather than a
narrow set, and the overlap is ordinary rather than exotic.

The cycle needs an organisation the config does NOT declare. A declared organisation cannot
produce it: `UpsertAssetsAsync` takes an exclusive lock on that asset row in step 1, before any
assignment lock, so either the importer holds the row and the delete blocks on its first
statement, or the delete holds it and the importer blocks in step 1 - in both orders the two lock
the asset row first and no cycle exists. For a non-declared organisation the importer touches the
asset row only in its final prune, which is what puts the two orders in opposition.

It is also NEW. Confirmed claim 11: the interleaving deadlocks against the fixed statement order
and InnoDB aborts one side with 1213, while the SAME interleaving against the current statement
order commits both sides. Today the delete also takes the assignment rows before the asset row,
matching the importer. Putting the locking read first is what inverts it.

**This is ACCEPTED rather than mapped or reordered**, and it is recorded under Risks alongside
the other importer risks. Four reasons, in the order that decides it:

1. Neither outcome can orphan anything. A deadlock aborts one transaction whole, so there is no
   partial write on either side and Decision 4's conclusion is untouched. In the probe the
   importer committed, the organisation and its assignments were gone together, and no dangling
   subject or parent was left.
2. Both sides already answer it. On the app side 1213 is one of the two codes this change maps,
   so the caller gets the retryable 409 that is the correct answer. On the CLI side `GitOpsCommands`
   already wraps `ImportAsync` in a `catch` for `DbException`, and `MySqlException` is a
   `DbException`, so a deadlocked import prints `Database operation failed: <server message>` and
   exits 3, the code that already means an operational failure that wrote nothing.
3. The app delete is the likelier victim. InnoDB rolls back the transaction that has changed the
   fewest rows, and a full import changes many more than a five-statement delete. The probe chose
   the delete. Likely, not guaranteed - the design accepts either.
4. `gitops sync` is one all-or-nothing transaction and is re-runnable by design. A rolled-back
   import is a loud failure the operator repeats, not a state to reconcile.

**Considered and rejected: mapping lock failures inside the importer.** The importer maps no MySQL
error code at all today, and `ImportAsync` returns `ImportResult`, which carries no failure shape.
A mapping therefore means a new result or exception type, a CLI branch to answer it, and a retry
or exit-code policy - a new failure vocabulary on a CLI path, bought for a message the CLI already
prints and an exit code it already returns. Rejected on `code-as-liability.md`.

**Considered and rejected: changing the lock order on either side.** The delete's locking read
cannot move; being first is the whole mechanism (Decision 1). The importer's prune order cannot
move; the RESTRICT foreign key requires assignments before assets. What is left is having the
importer take the asset locks it will eventually need BEFORE its assignment prune, as a locking
read over the absent declared assets. That adds a locking whole-table scan to a CLI path, converts
a fast deadlock into a long wait that can still end in an unmapped lock-wait timeout, and adds an
ordering rule nothing enforces. More code, same class of loud failure.

This carries no spec delta, and the check is recorded rather than assumed. The `compliance-write`
promise is bounded to writes that go through the app's write store, and the importer is not one.
The delete side of the deadlock is a retryable conflict, which the delta already permits and
already forbids reporting as a store failure. Neither outcome leaves an orphan, so the clause that
forbids the pair both taking effect is untouched.

#### Which writers catch it

`DeleteOrganisationAsync`, `UpsertOrganisationAsync`, `UpsertScopeTargetAsync`, and
`DeleteScopeTargetAsync` catch `MySqlException` with the two lock error codes and return
`WriteResult.Conflict`. The endpoint's `RunAsync` already maps a conflict to HTTP 409, which is
the correct answer to a retryable lock failure.

`DeleteScopeTargetAsync` is on that list because its multi-table `DELETE` joins `assets` and so
takes a lock on the organisation's row (the writer table above). It took that lock before this
change too, but only as an autocommitted single statement against a delete that held the row for
one statement; the delete now holds it from its first statement, so the window a scope delete can
block in is the whole guard. It has no transaction of its own, so the catch wraps its one
statement rather than a transaction body.

`AssignOrganisationRoleAsync` is added to that list, and the reason is the same argument that
justifies the other four, applied to a path that is worse off without it. Its endpoint,
`RoleAssignmentEndpoints`, has no exception handling of its own, and the application registers
no exception-handling middleware, so anything the method does not catch is answered as a bare
HTTP 500. Today it catches only `MySqlErrorCode.DuplicateKeyEntry`.

It needs TWO mappings, not one, and getting this wrong would leave the 500 in place while
looking like it had been removed. The new lock reaches this path in two different ways
(confirmed claim 7):

- **The delete commits while the assign waits.** The assign resumes, its foreign key is
  re-checked as a current read against a parent row that is gone, and it fails with 1452 (or
  1216 on the same condition). This is the shape PRODUCTION produces. The delete is five short
  statements and commits promptly, so an ordinary assign blocked behind it resumes long before
  its own lock wait could expire. This change makes that outcome far more reachable than it was:
  the delete now holds the asset row from its FIRST statement rather than its last, so a
  concurrent assign reliably queues behind the whole delete instead of overlapping it.
- **The assign's own lock wait expires first.** It fails with 1205. That needs the delete to
  stay open longer than the waiter tolerates, which is the raced test's arrangement (Decision 6
  gives the racing session a low `innodb_lock_wait_timeout` on purpose). It is not the common
  production shape.

The two get different results, because they are different answers to the caller:

- 1205 and 1213 return `AuthzWriteResult.Conflict`. The write lost a race that a retry can win.
- 1216 and 1452 return `AuthzWriteResult.Invalid`. The parent row is permanently gone, so a
  retry cannot succeed and a retryable answer would send the caller into a futile loop. Invalid
  is also the answer the method ALREADY gives for this condition when it is observed in time -
  its organisation-exists check returns `Invalid` when the organisation does not resolve - so
  mapping the foreign-key failure to `Invalid` makes the raced outcome identical to the
  sequential one instead of making it depend on timing.

The message on the `Invalid` branch names the reference generically rather than asserting the
organisation. The insert has three foreign-key parents (the user, the role, and the
organisation), and all three of the method's existence checks ahead of it are plain consistent
reads, so any of the three can be removed between check and insert. Discriminating by
constraint name would mean parsing a server message to sharpen a rejection the caller resolves
the same way regardless.

The fix is small because the shape already exists. `AuthzWriteResult` already carries `Conflict`
and `Invalid` statuses with messages, and `MapAssign` already answers both. So this is a widened
`when` clause on a catch the method already has, not a new result type, a new mapping, or a new
endpoint branch.

The asymmetry that makes this authz-only is worth stating, because it explains why the
compliance paths need no equivalent. `ComplianceWriteEndpoints.RunAsync` already catches
`DbException` on the integrity-constraint SQLSTATE (23000) and answers 409, and 1452 carries
that SQLSTATE. So a foreign-key failure on a compliance write already reaches a real HTTP
answer without the store mapping it. The authz endpoint has no such catch, which is the assign
path's hole.

It is the assign path's hole and not the authz surface's, and the difference is worth stating so
the scope is not overread. `RevokeOrganisationRoleAsync`, `RevokeSystemRoleAsync` and the
custom-role writes map neither 1205 nor 1213 either, and their endpoints have no exception
handling either, so a lock failure on any of them is also a bare 500. Those holes are
PRE-EXISTING and this change leaves them exactly as it found them: the delete's new lock is on
the asset row, and the assign is the only one of them that touches it. Widening the mappings to
paths this change does not make block would be hardening on speculation.

It carries no spec delta, and should not. A role assignment is not one of the references the
delete guard refuses over - the delete prunes assignments rather than counting them - so this
does not widen the `compliance-write` promise. The endpoint's documented behaviour is unchanged
too: a conflict there already means 409, and an invalid result already means 422. What changes
is only that these failures now reach those existing answers instead of escaping as unhandled
exceptions.

One positive is worth recording, because it points the same way: the lock also CLOSES a
pre-existing race. Today an assignment committed between the delete's prune and its asset
delete makes the final `DELETE FROM assets` fail the `ON DELETE RESTRICT` foreign key,
surfacing as a raw integrity violation rather than as a guard decision. With the delete holding
the asset row from its first statement, the assignment cannot commit in that window at all.

The alternative - leave a lock-wait timeout unmapped so it stays a store failure, on the
grounds that a timeout may mean a stuck transaction rather than a lost race - is rejected, and
the code settles it rather than judgment. `ComplianceEndpoints.IsStoreFailure` matches
`DbException`, and `MySqlException` is a `DbException`. An unmapped lock failure therefore
returns the UNREACHABLE-store response. That answer is factually wrong: the store was reached,
it did the work, and it declined to wait any longer. Telling the caller the store is
unavailable sends it away from the one action that resolves the situation, which is to retry.
Nothing in the error distinguishes a stuck transaction from a lost race anyway, so there is no
information to preserve by splitting them.

No retry loop. The caller retries, as it already does for every other conflict this store
returns.

### Decision 4: The CLI importer needs no change

`MySqlGitOpsImporter` runs in the CLI. A lock the web app takes does not reach it unless it
takes one too. It does not have to, and the reason is its own statement order: it upserts the
declared asset rows BEFORE it replaces the scopes, and it deletes absent declared assets after
both. `UpsertAssetsAsync` runs first, `ReplaceScopesAsync` second, and
`DeleteAbsentDeclaredAssetsAsync` last.

The upsert really does lock the rows the later scope insert names. Its `INSERT ... ON
DUPLICATE KEY UPDATE` takes an exclusive lock on each existing declared row, and that lock and
the delete's `FOR UPDATE` block each other in both directions. So for a scope the import
writes, the subject falls into exactly one of these cases:

- **A `Company`/`Department` declared in the config, whose row ALREADY EXISTS.** The importer
  holds an exclusive lock on its row from `UpsertAssetsAsync` onward, held until commit. An app
  delete of that organisation blocks on its first statement until the import commits, then
  current-reads the row the import just wrote and proceeds against a live organisation, with
  its counts seeing the imported scopes. If the app delete gets there first, the importer's
  asset upsert blocks on the delete's lock, and afterwards recreates the organisation from the
  config. Either way, no scope the import wrote is left dangling.
- **A `Company`/`Department` declared in the config, whose row DOES NOT EXIST yet.** The delete
  takes no RECORD lock here, because there is no record. It does not take nothing: the delete
  runs at the server default, which is `REPEATABLE READ`, so its `FOR UPDATE` takes an `X,GAP`
  lock at the adjacent existing id (claim 10), and the importer's insert of that id inside the
  gap blocks until the delete commits. (`READ COMMITTED` is `MySqlAssetWriteStore`'s explicit
  level, not the delete's, and it would not exempt the importer anyway - the gap lock belongs to
  the holder.) The conclusion survives on a narrower claim than "the import just proceeds
  later". Blocking changes when the import proceeds and CAN FAIL IT: the importer maps no lock
  error, so an insert that waits past `innodb_lock_wait_timeout` ends the run. That is the
  accepted risk recorded under Risks, and this bullet does not deny it. What blocking cannot do
  is make the import leave an ORPHANED subject or parent, which is the only thing Decision 4
  has to establish. There is no committed organisation for the delete to remove, so the delete
  removes none. Either the import fails and writes nothing, or it proceeds and creates the row
  together with the scopes and children that name it. The scope is not orphaned because the
  subject the import wrote is the subject the import created.
- **An organisation the config does NOT declare.** `DeleteAbsentDeclaredAssetsAsync` runs
  `DELETE FROM assets WHERE source = 'declared' AND id NOT IN @KeepIds` in the same
  transaction, so the import removes that organisation itself. Any scope naming it was going
  to dangle whether or not the app deleted anything.
- **A `Vendor`, a `Machine`, or an id naming no row.** `DeleteOrganisationAsync` deletes only
  `type IN ('Company', 'Department')`, so the app delete is never the writer that removed such
  a subject. A `Vendor` belongs here rather than in the declared case above even though the
  importer upserts it in the same batch as the organisations, and even though it is a legal
  scope subject: the app delete cannot touch it, so the race this design is about does not
  arise for it. No discovered organisation exists to complicate the `Machine` half: the only
  discovered writer produces `Machine` rows.

The `assets.parent` half is the same cases, because the parent of a declared child is either
declared (locked in the same batch, or created by the import when it did not exist) or absent
from the config (pruned by the import itself). A `Vendor` cannot be a parent, so that case
collapses.

Waiting is not the only new interaction between the two. The delete's lock order also makes a
DEADLOCK between them possible, which Decision 3 works through and accepts. That is a failure
mode, not an orphan: the aborted side writes nothing, so none of the four cases above changes.

**Considered and rejected: taking `FOR SHARE` locks in the importer on the existing asset rows
its scope subjects and child parents name, in sorted id order.** Two reasons. First, it is
redundant against the case analysis above: for a declared subject the exclusive lock from
`UpsertAssetsAsync` is already strictly stronger and already held, and for a non-declared
subject the import is itself the writer that removes the row, so a lock changes nothing.
Second, and decisively, it would not change any observable outcome even where it applies. The
importer tolerates a dangling subject and a dangling parent by contract and reports them as
non-blocking warnings, so a lock that made it WAIT and then observe the row gone would leave
it inserting the same dangling row it inserts today. Adding lock acquisition, an ordering
discipline to avoid deadlocks between imports, and a new blocking failure mode to a CLI path,
for an outcome that does not move, is the speculative hardening `code-as-liability.md` rules
out. If the importer is later made strict about subjects, that is the change that should carry
the locks, together with the rejection behaviour that gives them a purpose.

A secondary point, not relied on above: a GitOps-managed instance normally sets
`GitOpsOptions.ReadOnly`, and `GitOpsReadOnlyMiddleware` answers every unmarked mutating
request with 409. In that configuration the app delete is unreachable over HTTP at all. The
argument above deliberately does not lean on this, because an operator can run `gitops sync`
against an instance that is not in read-only mode.

One correction to the investigation is worth recording, because it changes what the importer's
self-check can be relied on for. `FindUnresolvedScopeSubjectsAsync` runs as a plain consistent
read inside the import transaction, so it answers from a read view that may predate a
concurrent app commit. It reports subjects the IMPORT itself left unresolved. It does not
detect an organisation another process deleted mid-import. The argument above does not lean on
it detecting that, and no argument should.

### Decision 5: Do not pin the delete's isolation level

Considered and rejected. The delete calls `BeginTransactionAsync(ct)` with no isolation
argument, taking the server default. The correctness argument in Decision 1 rests on record
lock compatibility and on locking reads being current reads. Both hold at READ COMMITTED,
REPEATABLE READ, and SERIALIZABLE. The read-view timing that Decision 1 property 2 needs also
holds at READ COMMITTED, where it is if anything stronger: a fresh snapshot per statement can
only make the guard's counts MORE current, never less. Read-view timing only tightens as the
level rises.

The unfixed guard is unsound at EVERY level, so there is no level at which the fix is
unnecessary. Only ONE MANIFESTATION of the defect is isolation-specific: the stale count, where
a racing write lands after the delete's first plain read and before its scope count, needs
REPEATABLE READ, because at READ COMMITTED the later count re-snapshots and sees it. The other
manifestation is level-independent. A racing write that commits after BOTH counts have returned
is invisible to the unfixed delete however it snapshots, since neither count runs again. That
window orphans a scope at READ COMMITTED just as it does at REPEATABLE READ (confirmed claim
5), and the fix closes it at both (claim 6).

The asymmetry therefore bounds only which negative check can use which statement order, not
whether the fix is needed. The verification group records both: the sweep against the wholly
unfixed store races the post-count window and so goes red at any level, while the narrower
check that moves the child count ahead of the locking read is REPEATABLE-READ-only. That
narrower check cannot be rewritten to use a later boundary instead, because with the locking
read in place the delete holds the asset row at every boundary after it, so a racing writer
blocks there rather than committing into the gap. Boundary 2 is the only hole that statement
order opens, and boundary 2 is exactly the stale-read case.

Pinning would therefore add a claim the design does not need and cannot justify, and it would
have a cost: an explicit level in this file would read as load-bearing to the next person, who
would then have to work out which property depends on it. None does. This is exactly the
argument that does NOT hold for option 2, whose gap locks disappear at READ COMMITTED, which
is one more reason to prefer option 1.

### Decision 6: Bound the interleave hook rather than add a command-start barrier

`InstrumentedConnectionFactory.BeforeCommandAsync` awaits the armed action to completion
before letting the Nth command reach the server. That is right for the existing tests, where
the racing writer never blocks, and its comment says so.

Once the delete holds a lock, the racing writer DOES block, on a transaction that is itself
suspended inside the hook waiting for that writer. Nothing breaks the cycle until
`innodb_lock_wait_timeout` expires, 50 seconds by default, and the test then fails for a
reason that has nothing to do with what it was asserting.

The mechanism is a bounded wait, in two parts, both deliberately small:

1. The racing action's session carries a low `innodb_lock_wait_timeout`. The blocked writer
   then fails fast with a lock-wait timeout, which the store maps to a conflict under Decision
   3, the hook's await returns, and the delete proceeds. The block is the observation: a writer
   that was NOT blocked completes in milliseconds. This lives in the test, not in the factory.
2. `BeforeCommandAsync` bounds its await of the armed action and, on expiry, throws naming the
   command ordinal it was interleaving before. This is the factory's change. It converts a
   deadlocked test from a slow, misattributed failure into a fast one that says where it hung.

The racing writer opens its own connection inside the store, so the test cannot reach that
connection to issue `SET SESSION`. Part 1 therefore needs a small connection-factory decorator
in `Freeboard.TestInfrastructure` that issues the `SET SESSION` on open.

The decorator is justified by WHICH failure it produces, not merely by the absence of a
connection-string option. Bounding the wait is not the hard part: `DefaultCommandTimeout` is a
connection-string setting that would also cap it, with no new type. What it would produce is a
client-side command timeout, and that is the wrong error. A session
`innodb_lock_wait_timeout` produces a SERVER lock-wait timeout - `ER_LOCK_WAIT_TIMEOUT` (1205),
arriving as a `MySqlException` - which is exactly the error shape Decision 3's mapping must
recognize and turn into a conflict. So the decorator is what makes the raced test exercise that
mapping end to end rather than merely avoid a hang. A client-side timeout would prove the test
does not hang while proving nothing about the store's behaviour under a real lock failure.

Setting the server GLOBAL instead is rejected: it is shared state, and the integration tests
run in parallel against one server, so it would change the lock-wait behaviour of every other
test.

Issuing `SET SESSION` on open is safe here for a reason worth recording, because it would not
be safe against a pooled factory. `MySqlTestDatabase` builds its connection strings with
`Pooling = false`, so every open is a fresh server session and the setting dies with the
connection. Nothing can leak into another test through a returned pooled connection.

**Considered and rejected: a command-start barrier in the factory** (a `WaitForCommandStart`
that lets the test start the competing writer, wait until it reaches its blocking lock, then
release the delete). It does not do what it needs to do. A command-start signal fires when the
command is dispatched, which is not the same as the server having begun to wait on a lock;
confirming the writer is actually BLOCKED would still need a poll of
`performance_schema.data_lock_waits` or a sleep, which is the thing the hook exists to avoid.
It also inverts the hook's design: its documented value is that the interleaving is a counted
command hook "with no sleep and no thread timing, so the test is deterministic", and a barrier
turns it into a two-thread rendezvous whose failure mode is a hang. The bounded wait keeps the
existing single-threaded contract and adds one cap.

The factory's class comment is updated to state the new contract: an armed action that may
block MUST bound its own wait, and the factory's cap is a diagnostic backstop, not the
mechanism. Leaving the comment claiming the action runs to completion would make it false.

The mechanism has one limitation, recorded so the coverage is not overread. Because the racing
writer times out rather than waiting for the delete to commit, the raced test never exercises
the tail of the delete-wins order, where the blocked writer resumes and is refused because the
organisation is gone. That tail is not a curiosity: on the role-assignment path it is the
outcome production usually produces (claim 7). Decision 7 reaches it without the hook's racing
slot - by a sequential case for the compliance writers, and by instrumenting the ASSIGN instead
of the delete for the role assignment.

### Decision 7: Test placement and shape

`tests/Freeboard.Persistence.Tests/`, a new file, beside the other MySQL integration tests and
gated the same way on `FREEBOARD_TEST_DB` through `MySqlTestDatabase.TryCreateAsync` and
`Skip.If`. The store is the unit under test; no web factory is needed, so this does not belong
with `ComplianceSnapshotConcurrencyTests` in `Freeboard.Web.Tests`.

The shape copies that file's method: measure how many statements the delete costs by arming
the hook with no action, then race the delete at EVERY statement boundary in turn rather than
hard-coding one. What is asserted is the pair of outcomes, not which one happened, because
both orders are legal:

- The racing write and the delete are never BOTH successful.
- The loser is refused with a RESULT, not an escaped exception. Whichever side loses returns a
  rejection or a conflict result from the store. A `MySqlException` reaching the test is a
  failure, because a lock failure that is not mapped to a conflict is the defect Decision 3
  exists to prevent, and nothing else in this change would catch it.
- After both settle, the database holds no scope whose `subject_id` names a missing
  organisation, and no asset whose `parent` names a missing organisation.
- Across the boundaries, both outcomes occur at least once. Without this the first assertion
  would hold on a fixture where the race never actually happens.

The fixture must be restored before every run, and this test needs that more than the file it
copies does. There both the read under test and the racing writer are repeatable; here BOTH
sides are destructive. The measuring run deletes the organisation outright. A writer-wins run
leaves the organisation present with a committed scope under it. A delete-wins run leaves the
organisation gone. So a sweep with no reset races nothing after its first run, and the
"both outcomes occur at least once" assertion could be satisfied by fixture exhaustion instead
of by the race - the failure mode that assertion was added to rule out. Each run therefore
re-seeds the pre-race state (the organisation present, no referencing scope, no child) before
it arms the hook, and asserts afterwards that it actually reached its boundary, the way the
template asserts the commands executed reached the boundary it raced.

The regression guard is the first boundary AFTER the locking read, and the test names that one.
The test cannot work out which command that is - it counts commands and cannot see what they
say - so the boundary is named as an ORDINAL: the locking read is the delete's first statement,
so the guard is boundary 2. Only that one number is written down; the sweep's upper bound stays
measured, so a delete that grows a statement still races every boundary it has.
There the delete already holds `FOR UPDATE`, so the racing writer must block and be refused,
and the delete must succeed with no orphan. Under a statement order that puts a plain read
ahead of the lock, the same boundary finds the delete holding no lock: the racing writer
proceeds, commits, and the delete's later count answers from a read view that predates it, so
both writes succeed and a scope is orphaned. That is the case a refactor has to break, and
asserting it by name is required, not optional.

Boundary 1 stays in the sweep, but as the ordinary writer-wins case rather than as the
statement-order guard. It cannot serve as that guard: with no command yet issued the delete has
taken no read view, so a plain count there is itself the first consistent read and sees the
racing commit. The unfixed store already rejects at boundary 1, so a test asserting it proves
nothing about statement order.

Two non-raced cases complete the coverage, and both are cheap:

- Write the reference to completion, then delete. The delete is rejected. This pins that the
  locking read did not weaken the plain guard.
- Delete to completion, then write the reference. The write is rejected because the
  organisation does not resolve. This is the tail Decision 6 cannot reach through the hook.

The child-organisation race gets the full treatment, not a spot check: its own boundary sweep,
its own orphaned-parent assertion, and its own spec scenario. The two halves of the guard fail
independently, and only the scope half has a compensating warning today, so the child half is
the one with less margin for a gap in coverage.

The role assignment gets its own two cases, because Decision 3 adds behaviour there that
nothing else in this plan executes. Reading the endpoint proves the mapping exists; it does not
prove the catch filter matches what the server sends, which is precisely what got the error
code wrong once already. The 1205 case is the sweep's shape: hold the delete at boundary 2 and
race the assign through the lock-wait decorator, and it must come back as a conflict RESULT.
The 1452 case cannot be reached that way at all - in that arrangement the racing session always
times out before the delete commits - so it inverts the instrumentation instead. Arm the hook on
the ASSIGN's connection factory at the command after its organisation-exists read - ordinal 4,
since the method issues exactly three plain reads before its INSERT and transactions are not
counted - with an armed action that runs the whole delete to completion. The ordinal is written
down for the same reason boundary 2 is: the hook takes a number, and an earlier arm passes
without executing the mapping. An arm at 3 at READ COMMITTED makes the organisation-exists check
itself see the row gone, so the method returns `Invalid` from its own guard and never reaches
the INSERT. The assign's INSERT then runs against a
committed delete, its foreign key is re-checked as a current read, and the store must answer
`Invalid` rather than let a `MySqlException` escape. That inversion needs no barrier and no
sleep: it is the existing counted-command hook pointed at the other transaction, and the
blocking that happens in production is incidental to the error - what produces 1452 is the
INSERT running after the delete committed, whether or not it waited to get there.

The interleaved action opens its connections from the UNINSTRUMENTED factory, as the existing
hook's contract requires: a connection taken from the instrumented one counts its commands there
and re-enters the hook. "Uninstrumented" is the word to use, because "undecorated" now collides
with the lock-wait decorator, which is a different decorator and orthogonal to this rule. Where
the action is meant to BLOCK, it is uninstrumented AND wrapped in the lock-wait decorator from
Decision 6, and nothing else; where it is meant to run to completion, as in the inverted
role-assignment case, it is uninstrumented and wrapped in nothing.

## Risks / Trade-offs

- **A referencing write now holds a shared lock on one asset row until it commits.** ->
  Shared locks are compatible with each other, so concurrent scope and organisation writes
  under the same organisation do not block one another. Only a delete of that organisation
  does, which is the intended serialization. When the id names an existing row the lock is on a
  single primary-key record, so the blast radius is that one row.
- **A delete of an ABSENT id locks a gap, not a record, and the writers it can now block do not
  map a lock-wait timeout.** -> The blast radius argument above does not cover this case, and it
  is reachable. `DeleteOrganisationAsync` returns success without checking rows affected (the
  defect listed as a non-goal), so an id that names no `Company`/`Department` row is not refused
  before the locking read - it reaches it. A sufficiently privileged caller gets there:
  `writes.MapDelete("/organisations/{id}")` gates on `org.write` against a resource built from
  the route id alone, and the store's `type IN (...)` predicate is defence in depth, not a
  refusal. That one cause splits into two risks with different blast radii - this bullet and the
  next - each judged on its own, and both closed by the same rows-affected check. At the
  server default the `FOR UPDATE` takes an `X,GAP` lock at the adjacent existing id (claim 10,
  first case), which blocks inserts of any id in that gap until the delete commits, and nothing
  else. Three writers insert into that primary-key space. `MySqlComplianceWriteStore.UpsertOrganisationAsync` is
  one, and this change maps 1205 on it. The other two are UNMAPPED and are named here rather
  than left implicit: `MySqlGitOpsImporter.UpsertAssetsAsync` catches no `MySqlException` at
  all, and `MySqlAssetWriteStore` catches only `DuplicateKeyEntry`, so a blocked insert in that
  window surfaces as whatever each caller does with an unmapped store exception. Running at
  `READ COMMITTED`, as `MySqlAssetWriteStore` does, does not exempt it: the gap lock is the
  DELETE's, and an insert-intention lock conflicts with a held gap lock whatever level the
  inserter runs at. This is ACCEPTED, not mitigated. The delete is short and the window is
  small, and both unmapped writers already tolerate a failed run (the CLI import is re-runnable,
  the asset write is a discovery upsert). Bounding the window by returning early when the
  locking read matches nothing is deliberately NOT done here: that is the rows-affected check
  wearing a different name, and it changes the delete's observable result, which this change is
  not the place to do. That check has its own tracking issue, which this change opens as a
  follow-up, and the form that closes THIS bullet is the early return on the locking read. A
  rows-affected check on the final `DELETE FROM assets` would answer not-found correctly and
  still hold the gap lock for the whole transaction, so it would close the result defect and not
  this one.
- **A delete of an id of the WRONG TYPE takes an exclusive RECORD lock on a live vendor or
  machine row.** -> This half is not a corollary of the one above and must not be read as one.
  The row exists, so the search finds and locks the record and the `type IN (...)` filter is
  applied afterwards: the lock is `X,REC_NOT_GAP` on a live row, not `X,GAP` (claim 10, second
  case). The radius is therefore wider than the two inserters above: for its
  whole transaction the delete exclusively holds an asset row that something else legitimately
  owns, blocking `MySqlAssetWriteStore`'s upsert of that machine row,
  `MySqlGitOpsImporter.UpsertAssetsAsync` for that vendor, and any insert into a child table
  that takes the shared foreign-key parent lock on it - `asset_source`, `collectors`,
  `integration_connections`, `vendor_assurances`. None of those paths maps 1205.
  Judged on its own rather than inherited from the absent-id case, this is still ACCEPTED, for
  three reasons that hold specifically for it. The hold is bounded by the delete's own
  transaction, which is five short statements with no caller interaction in it, so a blocked
  writer waits milliseconds and the 50-second lock wait it would have to exhaust to FAIL is
  unreachable without the app itself stalling. The request that causes it is an authoring error
  by a privileged caller - deleting an id that is not an organisation - not a load path any
  normal operation follows. And it needs no fix of its own: the rows-affected check closes this
  half and the absent-id half together, in the early-return form the tracking issue names, so
  nothing is gained by inventing a second, narrower guard for it here. What makes it accepted
  rather than ignored is that it is written down: a reader who sees a vendor upsert time out
  against an organisation delete finds the reason here.
- **A newly reachable deadlock between two organisation upserts that name each other as
  parent.** -> Decision 3 maps it to a conflict, so the caller sees a retryable 409 rather
  than a store failure. Such a pair of writes is also an authoring anomaly, not a normal load.
- **A long-running GitOps import blocks app organisation writes on the rows it has upserted.**
  -> Already true before this change: the import takes exclusive locks on those rows itself.
  The change adds a shared lock waiter, not a new exclusive one. The waiter now fails as a
  conflict instead of hanging to the default timeout.
- **An organisation delete and a GitOps import can now DEADLOCK, and the server aborts one of
  them.** -> New with this change, and ACCEPTED on the same footing as the importer risks above.
  The delete takes the organisation's asset row before its assignment rows; the importer takes
  assignment rows before asset rows, an order its own `ON DELETE RESTRICT` foreign key requires.
  For an organisation the config does not declare, the two can hold each other's locks and InnoDB
  aborts one side with 1213 (confirmed claim 11). It is narrower than the risks above: a deadlock
  aborts one transaction whole, so there is no partial write and no orphan; on the app side 1213
  is one of the two codes this change maps, so the caller gets a retryable 409; on the CLI side
  `GitOpsCommands` already catches `DbException` around `ImportAsync` and exits 3 with the
  server's message, so a lost import is a loud, re-runnable failure rather than an unhandled
  abort. InnoDB prefers to roll back the transaction that changed fewer rows, so the app delete is
  the LIKELY victim of the two. Mapping lock codes inside the importer, and reordering either
  side's locks, were both considered and rejected in Decision 3.
- **The guarantee depends on the importer's statement order, which nothing enforces.** ->
  Decision 4 rests on `UpsertAssetsAsync` preceding `ReplaceScopesAsync`. That order is
  already required by the importer's own foreign-key reasoning and is documented in its class
  comment, so it is not incidental. It is still an implicit dependency, and the honest
  statement of the boundary is in the spec delta: the promise is bounded to app-managed
  writes.
- **The bounded interleave wait could make an existing test flaky if the cap is too tight.**
  -> The cap is a diagnostic backstop set well above any current interleaved action's runtime,
  and the existing actions never block on a lock. The new test bounds itself with the session
  lock-wait timeout and does not rely on the cap.
- **The guarantee holds only for writers that go through the store.** -> Anything writing
  `scopes` or `assets` by another route (a direct SQL session, a future store) is not
  serialized. The spec delta states this boundary rather than leaving the promise sounding
  absolute.
- **The new test needs a real MySQL.** -> It skips cleanly without `FREEBOARD_TEST_DB`, like
  every other integration test, so `dotnet test` still passes with no database. This does mean
  CI proves it only where the database is configured.

## Migration Plan

No schema migration, no data change, no configuration change. The change ships in one
deployment. Rolling back is reverting the commits; the previous binary and the new binary can
run against the same database at the same time, since the only difference is which locks the
app takes.

## Open Questions

None.
