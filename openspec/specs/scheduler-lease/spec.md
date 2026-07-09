# scheduler-lease Specification

## Purpose
TBD - created by archiving change add-collector-scheduler. Update Purpose after archive.
## Requirements
### Requirement: Per-collector claiming via row locking

The system SHALL claim due collectors independently, one scheduler-state row at a time,
using `SELECT ... FOR UPDATE SKIP LOCKED` inside a short transaction so concurrent workers
claim disjoint collectors without blocking each other. A row that is due (`next_due_at` at or
before the current database time), not `dead`, present in the active integration collector
id set, and unleased (no lease owner, or an expired lease) SHALL be eligible; a row another
worker is mid-claim on SHALL be skipped, not waited on. On claim the worker SHALL set the
lease (owner, a freshly minted lease token, expiry, heartbeat), the run start time, and
`status='running'`, and SHALL assign the stable run id only if none is already in progress. At
most one worker SHALL hold a given collector's lease at a time.

The claim SHALL return, per claimed row, the effective post-update values
`ClaimedCollectorLease(CollectorId, LeaseToken, CurrentRunId, LeaseExpiresAt)`, read inside
the same transaction, because the lease token is minted and the run id is resolved
(`COALESCE`) inside the claim `UPDATE` and cannot be reconstructed by the caller; the fencing
lifecycle (heartbeat, complete, fail) depends on these returned values.

#### Scenario: The claim returns the minted lease token and resolved run id

- **WHEN** a worker claims a due collector
- **THEN** the claim returns that collector's id, the freshly minted lease token, the resolved
  `current_run_id`, and the lease expiry, so the worker can heartbeat and later complete under
  that fencing token

#### Scenario: Two workers claim disjoint collectors

- **WHEN** two workers run a claim cycle at the same time against several due collectors
- **THEN** each due collector is claimed by at most one worker and neither worker blocks on a
  collector the other is claiming

#### Scenario: A due, unleased collector is claimed with a fresh lease token

- **WHEN** a worker claims a due, unleased collector
- **THEN** the collector's row records that worker as owner with a newly minted lease token,
  an expiry in the future, and a start time

#### Scenario: A slow collector does not block others

- **WHEN** one collector's dispatch is slow and holds its lease
- **THEN** other due collectors are still claimed and dispatched by workers in the same or
  another replica

### Requirement: Lease token fences completion and rescheduling

The system SHALL fence every state transition after a claim (heartbeat renewal, success
completion, failure rescheduling) on the lease token, so only the current lease holder can
mutate a collector's run state. An update carrying a stale lease token SHALL affect no rows
and SHALL NOT overwrite the current holder's state.

#### Scenario: A stale lease token cannot complete a run

- **WHEN** a worker whose lease has expired and been reclaimed by another worker attempts to
  complete the run with its old lease token
- **THEN** the update affects no rows and the current holder's state is unchanged

#### Scenario: The current holder can complete its run

- **WHEN** the worker holding the current lease token completes its run
- **THEN** the completion succeeds and updates the collector's run state

### Requirement: A claimed lease can be released without recording a run outcome

The store SHALL expose a fenced release operation that drops a lease and sets `status` and
`next_due_at` without recording a run outcome, for the case where a claimed collector is not
actually dispatched (for example a null resolvable interval). The release SHALL clear the
lease columns (`lease_owner`, `lease_token`, `lease_expires_at`, `lease_heartbeat_at`), set the
supplied `status`, set `next_due_at` to the supplied value or leave it unchanged when none is
supplied, and SHALL leave `current_run_id` untouched because the collector was not run. It
SHALL be fenced on the lease token so only the current holder can release, and the completion
methods (which require a non-null interval) SHALL NOT be used for this path.

For the defensive null-interval skip the caller SHALL supply a non-claimable `status` (`dead`)
and no `next_due_at`, so the released row is excluded from future claims (the claim query
excludes `status='dead'`) and revived only by a config/frequency change or a manual reset. The
caller SHALL NOT release a still-due row into a claimable status with an unchanged past
`next_due_at`, which the claim query would re-select every poll interval.

#### Scenario: A claimed collector is released without completing

- **WHEN** a worker claims a collector but cannot dispatch it (its scheduling interval
  resolves to null) and releases the lease
- **THEN** the collector's lease columns are cleared, its `status` is set as supplied, its
  `current_run_id` is left unchanged, and no run success or failure is recorded

#### Scenario: The null-interval skip releases into a non-claimable status

- **WHEN** a claimed collector's scheduling interval resolves to null and the service releases
  its lease as the defensive skip
- **THEN** the release sets `status` to `dead` (a non-claimable status the claim query
  excludes) with `next_due_at` left unchanged, and the collector is not re-claimed on the next
  cycle until a config change or manual reset revives it

### Requirement: A crashed worker's lease expires and is reclaimed

The lease SHALL carry a time-to-live so a worker that crashes without releasing does not
wedge a collector's schedule. When a lease's expiry is in the past, another worker SHALL be
able to claim the collector with no manual intervention. Reclaiming SHALL preserve the
collector's `current_run_id`, so the retry reuses the same stable run token and the future
real runner can complete safely.

#### Scenario: An expired lease is reclaimed by another worker

- **WHEN** a collector is leased but its lease expiry is in the past (modelling a worker that
  crashed without releasing) and another worker runs a claim cycle
- **THEN** the other worker claims the collector, mints a new lease token, and keeps the
  existing `current_run_id`

### Requirement: Lease renewal keeps a long run's claim alive

The worker SHALL renew the lease on a heartbeat while a dispatch is in flight, extending the
expiry, fenced on the lease token. A renewal that affects no rows SHALL signal that the lease
was lost, and the worker SHALL cooperatively cancel the in-flight dispatch.

#### Scenario: Heartbeat extends the lease expiry

- **WHEN** the current holder renews its lease during a run
- **THEN** the lease expiry is extended and the run continues

#### Scenario: A lost lease cancels the in-flight dispatch

- **WHEN** a heartbeat renewal affects no rows because the lease was reclaimed
- **THEN** the worker cancels the in-flight dispatch instead of completing it

### Requirement: Lease time uses the database clock

All claim eligibility, lease expiry, and renewal comparisons SHALL use the database server
clock rather than any worker's local clock, so cross-replica clock skew cannot corrupt
claiming or leasing.

#### Scenario: Eligibility is judged by the server clock

- **WHEN** the claim query decides whether a collector is due and unleased
- **THEN** it compares `next_due_at` and `lease_expires_at` against the database server time,
  not the calling worker's clock

### Requirement: MySQL schema and migration for the scheduler state

A forward-only migration SHALL create the `collector_scheduler_state` table with a
`collector_id` primary key (`VARCHAR(190)` `utf8mb4_bin`), a non-null `next_due_at
DATETIME(6)`, a nullable `current_run_id CHAR(26)` `utf8mb4_bin` (a ULID, the future
idempotency key), the lease columns `lease_owner VARCHAR(190)` `utf8mb4_bin`, `lease_token
CHAR(26)` `utf8mb4_bin` (a ULID fencing token), `lease_expires_at DATETIME(6)`,
`lease_heartbeat_at DATETIME(6)`, the history columns `last_started_at`, `last_completed_at`,
`last_success_at`, `last_failure_at` (all nullable `DATETIME(6)`), a non-null `failure_count
INT` defaulting to 0, a nullable `last_error TEXT`, a non-null `status VARCHAR(16)`
`utf8mb4_bin` defaulting to `'pending'` (closed set `pending|running|ok|error|dead`), a
nullable `config_fingerprint CHAR(64)` `utf8mb4_bin`, and `created_at`/`updated_at
DATETIME(6)` each defaulting to `UTC_TIMESTAMP(6)` (with `updated_at ON UPDATE
UTC_TIMESTAMP(6)`), so an ensure insert cannot fail on the NOT NULL timestamps. It SHALL index
the claim path (at least `next_due_at`). It SHALL have no foreign key to `evidence_collectors`.
The migration SHALL be replay-safe using `CREATE TABLE IF NOT EXISTS`.

#### Scenario: Migration creates the scheduler-state table

- **WHEN** the migration runner applies the collector-scheduler migration to a database at
  the prior version
- **THEN** the `collector_scheduler_state` table exists with the columns and claim index
  above (including `status`, `config_fingerprint`, and the ULID `current_run_id`/`lease_token`
  columns), has no foreign key to `evidence_collectors`, and the migration is re-runnable
  without error

### Requirement: Distributed-claim integration tests are gated on FREEBOARD_TEST_DB

The MySQL integration tests for per-collector claiming SHALL run against the real database
named by `FREEBOARD_TEST_DB` and SHALL skip cleanly when that variable is unset, using the
existing `SkippableFact` / `RequiresEnvVarFact` and `MySqlTestDatabase` infrastructure. The
tests SHALL prove claim isolation (two workers never claim the same due collector), expiry
reclaim (a crashed worker's collector is reclaimed with its run id preserved), and fencing (a
stale lease token cannot complete or reschedule a run).

#### Scenario: Tests skip when the database is not configured

- **WHEN** `dotnet test` runs with `FREEBOARD_TEST_DB` unset
- **THEN** the scheduler-claim integration tests are skipped, not failed

#### Scenario: Tests run when the database is configured

- **WHEN** `FREEBOARD_TEST_DB` points at a reachable MySQL and `dotnet test` runs
- **THEN** the scheduler-claim integration tests provision a throwaway database, apply
  migrations, and exercise SKIP LOCKED claim isolation, lease renewal, expiry reclaim with
  run-id preservation, and stale-token fencing

