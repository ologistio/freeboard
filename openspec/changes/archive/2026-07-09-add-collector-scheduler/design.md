## Context

Evidence collectors are declared in GitOps config and persisted in `evidence_collectors`
with a `type` (one of `integration`, `script`, `manual-attestation`,
`training-attestation`, `agent`) and a `frequency` token (`continuous`, `daily`,
`weekly`, `monthly`, `quarterly`, `annual`). `IComplianceStore.GetEvidenceCollectorsAsync`
returns every collector with its type and frequency. A collector attaches to a control (its
attach point), and optionally names a vendor; it does not attach directly to an
organisation or a requirement.

`Freeboard.Core.GitOps.EvidenceCollectorFrequency` is a pure static policy that owns the
canonical frequency token set and maps each token to a `(window, grace)` pair, used today
only for staleness (`IsStale`, which fires past window + grace). The window is the maximum
expected interval between collections: `continuous` 1h, `daily` 1d, `weekly` 7d, `monthly`
31d, `quarterly` 92d, `annual` 366d.

Evidence is push-based today: agents and the CLI append runs through the ingest endpoint;
the service never initiates collection. There is no `BackgroundService` or `IHostedService`
anywhere in the solution. The web app (`Freeboard`) is the only host that references both
Core and Persistence, so an in-service scheduler must live there.

Persistence conventions: stores take `IDbConnectionFactory` and use Dapper; ids are
`VARCHAR(190)` `utf8mb4_bin`; migrations are hand-written embedded `NNN_slug.sql` files
applied by `MySqlMigrationRunner` (CLI-driven; the web app never runs migrations and opens
its connection lazily, so an empty connection string still boots). Time in the staleness
path is taken from an injected `TimeProvider`; the pure Core rule takes `nowUtc` as a
parameter so it stays clock-free.

Deployment: the web app runs as several replicas behind a load balancer. Each due collector
must run once across the fleet, failure of one collector must not block others, and a
crashed worker must not wedge a collector's schedule.

This design synthesizes two independent plans (the original planner's and Codex's reviewer
plan) under two binding mediator decisions. Each notable choice below names its source
(planner / Codex / mediator) and, where the plans diverged, how the divergence was
resolved.

All affected code is MIT: `Freeboard.Core` (pure interval helper), `Freeboard.Persistence`
(scheduler-state store, migration), and the web app (hosted service, no-op runner seam).
Nothing touches `Freeboard.Enterprise`; the reference graph (Persistence to Core, Core
references nothing, web combines both) is preserved. Agent and CLI are not touched.

## Goals / Non-Goals

**Goals:**

- Run due integration collectors on their declared `frequency` from a hosted background
  service in the web app.
- Claim each due collector independently so exactly one worker runs it and one slow
  collector never blocks the others.
- Recover automatically when a worker crashes: the collector's lease expires and another
  worker reclaims it, with a stable run token so retry is safe.
- Reuse the existing Core cadence map for the scheduling interval; do not duplicate it.
- Keep the actual integration execution behind a no-op seam, while baking crash-safety into
  the schema now so the future real runner is safe.

**Non-Goals:**

- Concrete integration execution or real evidence production (a follow-up change; see the
  deferred concerns H1 and M3 below).
- Scheduling non-integration collectors.
- A single global leader, or cross-replica work coordination beyond per-collector claiming.
- Cron / calendar-aligned scheduling or a scheduler UI.
- Web app auto-migration.

## Decisions

### D1 (mediator): Per-collector lease via `SELECT ... FOR UPDATE SKIP LOCKED`

Divergence: the planner proposed a single global leader (one `scheduler_leases` row; the
leader runs all due collectors). Codex proposed claiming each collector independently. The
mediator adopted Codex's per-collector design. Resolution: the single-leader table is
dropped entirely; there is one mutable scheduler-state row per collector.

Each scheduler cycle claims work in a short transaction:

```
START TRANSACTION;
SELECT collector_id FROM collector_scheduler_state
 WHERE next_due_at <= UTC_TIMESTAMP(6)
   AND status <> 'dead'
   AND collector_id IN @ActiveCollectorIds
   AND (lease_owner IS NULL OR lease_expires_at <= UTC_TIMESTAMP(6))
 ORDER BY next_due_at
 LIMIT @batch                -- @batch <= MaxDegreeOfParallelism (invariant, see D5/D7)
 FOR UPDATE SKIP LOCKED;
-- for each returned row, mint @newToken (ULID) and @newRunId (ULID), then:
UPDATE collector_scheduler_state
   SET lease_owner=@me,
       lease_token=@newToken,
       lease_expires_at=UTC_TIMESTAMP(6) + INTERVAL @ttlSeconds SECOND,
       lease_heartbeat_at=UTC_TIMESTAMP(6),
       last_started_at=UTC_TIMESTAMP(6),
       status='running',
       current_run_id=COALESCE(current_run_id, @newRunId)
 WHERE collector_id=@id;
-- read back the effective post-update row so the caller gets the resolved fencing values:
SELECT collector_id     AS CollectorId,
       lease_token      AS LeaseToken,
       current_run_id   AS CurrentRunId,
       lease_expires_at AS LeaseExpiresAt
  FROM collector_scheduler_state
 WHERE collector_id=@id;
COMMIT;
```

`FOR UPDATE SKIP LOCKED` makes two replicas claim disjoint rows without blocking each
other: a row another worker is mid-claim on is skipped, not waited on. This isolates
failure (a stuck collector holds only its own row) and needs no global coordinator. All
time comparisons use the database clock (`UTC_TIMESTAMP(6)`), so cross-replica clock skew
cannot corrupt leasing.

`current_run_id` is assigned only when null (`COALESCE`), so a crash retry reuses the same
stable run token rather than minting a new one; `lease_token` is minted fresh on every
claim and is the fencing token for that attempt. Both tokens are ULIDs generated via the
existing `IUlidFactory` and stored as `CHAR(26)` `utf8mb4_bin`, matching the repo id
convention (F-4).

The claim returns, per claimed row, a `ClaimedCollectorLease(string CollectorId,
string LeaseToken, string CurrentRunId, DateTime LeaseExpiresAt)`. These values are minted
or resolved inside the claim `UPDATE` (`lease_token` is freshly minted, `current_run_id` is
resolved by `COALESCE`), so the caller cannot reconstruct them; the trailing `SELECT` reads
the effective post-update row inside the same transaction and returns them. The fencing
lifecycle (heartbeat, complete, fail) is unimplementable without these returned values
(F-2).

Two filters keep the claim honest (F-3):

- `status <> 'dead'` excludes rows that have given up after `MaxAttempts` failures (D4/D10);
  they are not claimed again until reset or revived.
- `collector_id IN @ActiveCollectorIds` restricts claiming to the current integration
  collector ids read from `IComplianceStore` this cycle. A collector that was deleted from
  config, or whose `type` changed away from `integration` (for example integration to
  script), drops out of `@ActiveCollectorIds` and is therefore never claimed again, even
  though its state row lingers. Without this filter such rows stay due forever and would be
  leased on every cycle.

### D2 (mediator): No-op runner seam now, crash-safe schema now, real execution deferred

Divergence: both plans kept a runner seam; the planner leaned toward the seam being purely
a test seam, Codex emphasized crash-safety fields. The mediator bound both: ship the seam
with a no-op default AND bake crash-safety into the schema now.

- `IScheduledCollectorRunner.RunAsync(EvidenceCollectorRow collector, string runId,
  CancellationToken)` is the dispatch target. `LoggingScheduledCollectorRunner` logs the
  dispatch and returns; it does not append evidence. The `runId` is the stable
  `current_run_id`, passed so the future real runner can make its append idempotent on it.
- Completion is fenced by the lease token so only the current claim can finish or reschedule
  a run: `UPDATE ... WHERE collector_id=@id AND lease_token=@token`. A worker that lost its
  lease (expired and reclaimed) affects 0 rows and does not overwrite the new holder's state.

Deferred concerns, documented here and owned by the runner-execution change, NOT solved
now:

- H1 (unit of work): a collector attaches to a control, not directly to an organisation or
  requirement. The real runner must resolve, from the control attach point, which
  organisations and requirements an appended evidence run applies to. The scheduler here
  schedules by collector id only and does not resolve that fan-out.
- M3 (vendorless integration collectors): an integration collector with no vendor cannot
  currently append evidence through the existing append path (which is keyed partly on
  vendor). The real runner change must decide how a vendorless integration collector's
  evidence is attributed. The scheduler here still schedules such collectors (they are valid
  integration collectors); the no-op runner simply logs.

### D3 (Codex): Public base-interval helper on Core, separate from staleness grace

`EvidenceCollectorFrequency` gains a public `Interval(string? frequency)` returning the
cadence window as the scheduling interval (`continuous` 1h ... `annual` 366d), or null for a
null/blank/unknown token. This reuses the existing `Cadences` dictionary; no second
vocabulary is introduced. Staleness keeps using interval + grace; scheduling uses the plain
interval. The planner had folded due-ness into an `IsDue` helper; the resolution keeps the
smaller, more composable `Interval` accessor (Codex's shape) because due-ness in this design
is a stored `next_due_at` comparison done in SQL, not an in-memory subtraction, so the
service needs the interval value to advance `next_due_at`, not a boolean.

### D4 (Codex): One mutable scheduler-state row per collector; due-ness is `next_due_at`

One row per collector in `collector_scheduler_state` holds the schedule, the in-flight run,
the lease, and health fields (full shape in the schema section). Due-ness is
`next_due_at <= UTC_TIMESTAMP(6)`, evaluated in the claim query on the database clock.

**Status column and its closed value set.** The row carries an explicit `status`
(`VARCHAR(16)` `utf8mb4_bin`) for observability and for the terminal dead-letter state. The
value set is closed:

| status    | meaning                                                                 |
| --------- | ----------------------------------------------------------------------- |
| `pending` | ensured/reset, waiting for `next_due_at`; claimable when due             |
| `running` | leased and dispatching now                                              |
| `ok`      | last run succeeded; claimable again at the next `next_due_at`           |
| `error`   | last run failed, will retry after backoff; claimable again when due     |
| `dead`    | gave up after `MaxAttempts` failures; NOT claimed again until reset     |

Transitions (all post-claim transitions are fenced on `lease_token`):

- `(none) -> pending` on ensure.
- `pending|ok|error -> running` on claim.
- `running -> ok` on success.
- `running -> error` on failure when `failure_count + 1 < MaxAttempts`.
- `running -> dead` on failure when `failure_count + 1 >= MaxAttempts`.
- `dead|error -> pending` on revival (manual reset, or a config/frequency change detected by
  ensure; see below).

The claim query excludes `status = 'dead'` (D1), so a dead collector stops retrying and is
surfaced (via `status`, `last_error`, `last_failure_at`) instead of retrying forever.

Life cycle:

- Ensure (planner + Codex): before a cycle, upsert a state row for each `integration`
  collector. A missing row is inserted `status='pending'`, `failure_count=0`, `next_due_at =
  UTC_TIMESTAMP(6)` so a new collector is immediately due. The upsert also carries a
  `config_fingerprint` (a hash over the scheduling-relevant config: at least the `frequency`
  token and `type`). On an existing row ensure applies these rules by stored `status`:
  `running` -> untouched (never mutate its lease or run token); `pending`/`ok` -> update the
  stored `config_fingerprint` only when it differs (no other change); `error`/`dead` with a
  differing fingerprint -> update the fingerprint AND revive: `status='pending'`,
  `failure_count=0`, `last_error=NULL`, and (for a revived `dead` row) `next_due_at =
  UTC_TIMESTAMP(6)`. This is the "config/frequency change resets a dead collector" path.
  Refreshing the fingerprint on a changed `pending`/`ok` row too (defect F-12b) prevents a
  stale-fingerprint row from being wrongly re-revived on every later cycle, which would keep
  resetting `failure_count` and prevent it ever reaching `dead`. Rows for collectors removed
  from config or type-changed are excluded from claiming by the active-collector filter (D1),
  not by ensure. The upsert is one `INSERT ... ON DUPLICATE KEY UPDATE` using the MySQL
  8.0.20+ aliased-row form (`... AS new ...`, the target is MySQL 8.4, not the deprecated
  `VALUES()`):

  ```
  INSERT INTO collector_scheduler_state
         (collector_id, next_due_at, status, failure_count, config_fingerprint)
  VALUES (@id, UTC_TIMESTAMP(6), 'pending', 0, @fingerprint) AS new
  ON DUPLICATE KEY UPDATE
      -- Order matters: MySQL evaluates the SET list left-to-right and each assignment sees
      -- columns already updated to their left. Every revived column and the fingerprint gate
      -- on the PRE-update status and config_fingerprint, so failure_count/last_error/next_due
      -- come first, status next, and config_fingerprint LAST. If config_fingerprint were
      -- assigned first, `config_fingerprint <> new.config_fingerprint` would compare the row
      -- to itself (always false) and revival would never fire (F-12a).
      failure_count = IF(status <> 'running'
                         AND config_fingerprint <> new.config_fingerprint
                         AND status IN ('dead','error'),
                         0, failure_count),
      last_error = IF(status <> 'running'
                      AND config_fingerprint <> new.config_fingerprint
                      AND status IN ('dead','error'),
                      NULL, last_error),
      next_due_at = IF(status <> 'running'
                       AND config_fingerprint <> new.config_fingerprint
                       AND status = 'dead',
                       UTC_TIMESTAMP(6), next_due_at),
      status = IF(status <> 'running'
                  AND config_fingerprint <> new.config_fingerprint
                  AND status IN ('dead','error'),
                  'pending', status),
      -- Every non-running row whose config changed gets its stored fingerprint refreshed,
      -- not just revived rows (F-12b). Assigned LAST so the gates above read the old value.
      config_fingerprint = IF(status <> 'running', new.config_fingerprint, config_fingerprint);
  ```
- Claim (D1): sets the lease and `last_started_at`, assigns `current_run_id` if absent, sets
  `status='running'`.
- Complete success (fenced): `next_due_at = UTC_TIMESTAMP(6) + Interval`, set
  `last_completed_at = UTC_TIMESTAMP(6)`, `last_success_at = UTC_TIMESTAMP(6)`,
  `status='ok'`, `failure_count=0`, `last_error=NULL`, clear `current_run_id` and the lease.
  `next_due_at` derives from `UTC_TIMESTAMP(6)` (the completion time is DB-now, so this is
  exactly completion + interval), NOT from the co-assigned `last_completed_at` column. This
  keeps the same SET-order discipline the failure `UPDATE` and the ensure ODKU apply: MySQL
  evaluates a single-table `UPDATE` SET list left-to-right, so reading a co-assigned column
  (`last_completed_at = UTC_TIMESTAMP(6)` listed before `next_due_at`) would depend on its
  assignment order; using `UTC_TIMESTAMP(6)` directly avoids reading a stale prior value.
- Complete failure (fenced): keep `current_run_id` (so the retry reuses the stable token),
  release the lease, increment `failure_count`, set `last_failure_at`, `last_error`. When the
  incremented `failure_count` reaches `MaxAttempts` set `status='dead'` (terminal; the row is
  no longer claimed and `next_due_at` is irrelevant); otherwise set `status='error'` and
  `next_due_at = UTC_TIMESTAMP(6) + backoff`. All of this is one fenced `UPDATE`; the dead
  vs error decision and the backoff are computed in SQL from the pre-increment
  `failure_count`. MySQL evaluates a single-table `UPDATE` SET list left-to-right and later
  assignments observe already-updated columns, so `failure_count = failure_count + 1` is the
  LAST assignment: `status`, `next_due_at`, and `POW(2, failure_count)` above it all read the
  pre-increment value. `failure_count + 1 >= @maxAttempts` therefore tests "the incremented
  count reaches `MaxAttempts`" (with `MaxAttempts=2` the row goes `dead` on the second
  failure, not the first) and `POW(2, failure_count)` uses the pre-increment exponent (so the
  first failure backs off `BaseBackoff * 2^0 = BaseBackoff`):

  ```
  UPDATE collector_scheduler_state
     SET last_failure_at = UTC_TIMESTAMP(6),
         last_error = @error,
         current_run_id = current_run_id,          -- retained
         lease_owner = NULL, lease_token = NULL,
         lease_expires_at = NULL, lease_heartbeat_at = NULL,
         status = IF(failure_count + 1 >= @maxAttempts, 'dead', 'error'),
         next_due_at = IF(failure_count + 1 >= @maxAttempts,
                          next_due_at,               -- dead: unchanged, never claimed
                          UTC_TIMESTAMP(6)
                            + INTERVAL LEAST(@intervalSeconds,
                                             @baseBackoffSeconds * POW(2, failure_count))
                              SECOND),
         failure_count = failure_count + 1          -- LAST: rows above read pre-increment
   WHERE collector_id=@id AND lease_token=@token;
  ```

- Release lease (fenced, no completion): `ReleaseLeaseAsync(collectorId, leaseToken, status,
  nextDueAt?)` drops the lease without recording a run outcome, for the null-interval skip
  (F-6). It does not touch `current_run_id` (the collector was not actually run) or the
  failure/history counters:

  ```
  UPDATE collector_scheduler_state
     SET lease_owner = NULL, lease_token = NULL,
         lease_expires_at = NULL, lease_heartbeat_at = NULL,
         status = @status,
         next_due_at = COALESCE(@nextDueAt, next_due_at)
   WHERE collector_id=@id AND lease_token=@token;
  ```

**Backoff formula (F-5, was an open question).** Bounded exponential:
`backoff = min(interval, BaseBackoff * 2^failure_count)` where `failure_count` is the
pre-increment value (0 on the first failure). Base `BaseBackoff` defaults to 60s; the cap is
the collector's own scheduling interval, so a failing collector never retries slower than it
would normally run. The formula is deterministic and evaluated inside the fenced SQL `UPDATE`
from `failure_count`, so no precomputed backoff is passed in and the store does not need to
expose `failure_count` to the service. `CompleteFailureAsync(collectorId, leaseToken, error,
interval, baseBackoff, maxAttempts)` carries the inputs; the previous precomputed `backoff`
parameter is removed.

Because `next_due_at` is a single timestamp advanced from the completion time, a collector
overdue by many intervals runs exactly ONE catch-up run and is then scheduled one interval
out - no per-missed-interval flood at startup (Codex; both plans agreed once raised).

The scheduling interval is the plain window, not window + grace: grace is a staleness
tolerance, not a scheduling delay. Firing at the interval keeps evidence refreshed before it
can cross the staleness threshold (interval + grace).

**Null-interval guard (F-6, F-13).** `frequency` is `NOT NULL` in migration 012 and
`ConfigValidator` rejects unknown tokens, so for validated data `EvidenceCollectorFrequency.
Interval` is always non-null; this whole guard is defensive. Two layers keep a null interval
from ever tight-looping, and BOTH are specified:

- Primary (never claimed). A null-interval collector is kept out of claiming entirely.
  `EnsureScheduledAsync` does NOT seed (or revive) a state row for a collector whose
  `EvidenceCollectorFrequency.Interval` is null - the service filters such collectors out of
  the ensure input - and the service also excludes null-interval collector ids from the
  `@ActiveCollectorIds` set it passes to the claim query. A collector whose interval became
  unresolvable is therefore filtered out of claiming (via D1's active-collector filter) rather
  than claimed-then-released.
- Defensive (claimed-then-released). If a collector is nonetheless claimed and its `Interval`
  resolves to null, the service skips it with a warning log and releases the lease via
  `ReleaseLeaseAsync` (above) with `status='dead'` - a non-claimable status (the claim query
  excludes `status='dead'`, D1), NOT a claimable status left with a past `next_due_at` that the
  claim query would re-select every `PollInterval`. The `dead` release excludes the row from
  future claims, surfaces it for inspection, and revives it only on a config/frequency change
  (`config_fingerprint`, detected by ensure) or a manual reset - exactly like a collector that
  exhausted `MaxAttempts`. It is not completed with a null interval (which
  `CompleteFailureAsync`/`CompleteSuccessAsync` cannot express, since both require a non-null
  interval) and not tight-looped. The release leaves `current_run_id` as-is because the
  collector was never dispatched.

### D5 (Codex + mediator): Heartbeat renewal with cooperative cancellation

The worker renews the lease on a heartbeat at roughly TTL/3 while a dispatch is in flight:
`UPDATE ... SET lease_heartbeat_at=UTC_TIMESTAMP(6), lease_expires_at=UTC_TIMESTAMP(6) +
INTERVAL @ttl SECOND WHERE collector_id=@id AND lease_token=@token`. Rows-affected 0 means
the lease was lost (expired and reclaimed); the worker cancels the in-flight dispatch
through a `CancellationTokenSource` linked to the host stopping token, so a superseded worker
stops promptly rather than finishing and racing the new holder. Even though the runner is a
no-op now, the cancellation seam is wired so the future real runner honors it (M1).

**Claimed-row heartbeat invariant (F-1).** A claimed row's lease must be continuously
renewed from the moment of claim until its run completes; otherwise a row that is claimed but
still queued (waiting for a dispatch slot) can have its lease lapse before its heartbeat
starts, another worker reclaims it, and the collector is double-dispatched. We enforce this
by bounding `BatchSize <= MaxDegreeOfParallelism` (see D7): the service claims at most as
many rows as it can dispatch immediately, so every claimed row starts its heartbeat at claim
time with no queuing gap. The effective batch is `min(BatchSize, MaxDegreeOfParallelism)`, so
the invariant holds even if the two options are misconfigured.

**Heartbeat shutdown ordering (F-10).** For each dispatched collector the service stops and
awaits the per-row heartbeat loop BEFORE issuing the fenced completion (success or failure).
If a heartbeat renewal ran concurrently with a completion, a late renewal firing just after a
successful completion cleared the lease would see 0 rows and falsely log "lease-lost". Draining
the heartbeat first removes that race; completion is the last write for the row.

### D6 (mediator + planner): Migrations stay CLI-driven; scheduler degrades gracefully

The web app does not run migrations (unchanged). If `collector_scheduler_state` is missing
(migration 016 not yet applied), the claim query fails; the scheduler catches ONLY the
specific missing-table error - `MySqlException` with `ErrorCode ==
MySqlErrorCode.NoSuchTable` (MySQL 1146, `ER_NO_SUCH_TABLE`) - logs a clear one-line warning,
and backs off
(sleeps a longer interval and retries) rather than crashing the loop or the app. It does NOT
use a broad `catch`: transient connection or timeout errors must not be swallowed as
"table missing", so any other exception propagates to the loop's normal error handling. The
lazy empty-connection-string startup
is preserved: when `Enabled` is false or the connection string is empty, the service returns
immediately without touching the database, so the app still boots and tests are unaffected
(M2).

### D7: Hosted service shape and configuration

`CollectorSchedulerService : BackgroundService` runs one loop:

1. If disabled or the connection string is empty, return immediately.
2. Read `integration` collectors (`IComplianceStore`); ensure a state row for each collector
   with a resolvable interval. A null-interval collector is not seeded and its id is excluded
   from the `@ActiveCollectorIds` set passed to the claim (D4, F-13).
3. Claim a batch of due collectors (D1).
4. For each claimed collector: start a heartbeat (D5), dispatch through the runner passing
   `current_run_id` and the linked cancellation token; then stop and await the heartbeat
   (F-10) and complete-success or complete-failure (fenced, D4). A claimed collector whose
   interval is null (a purely defensive case, since such ids are already excluded from claiming
   per step 2 / D4) is skipped and its lease released via `ReleaseLeaseAsync` with
   `status='dead'` (F-6, F-13) - a non-claimable status, so the row is not re-selected every
   `PollInterval` and is revived only by a config change or manual reset - dropping the lease
   without recording a run outcome.
5. Sleep the poll interval; loop until the host stops. On stop, in-flight dispatches are
   cancelled and their leases are left to expire (no forced completion of partial work).

`SchedulerOptions` (bound from `Freeboard:CollectorScheduler`): `Enabled` (default true in
production; the web test factories set it false), `PollInterval` (default 30s), `LeaseTtl`
(default 90s), `MaxDegreeOfParallelism` (concurrent dispatches, default 4), `BatchSize` (max
collectors claimed per cycle, default 4), `MaxAttempts` (failures before a collector goes
`dead`, default 5), `BaseBackoff` (exponential backoff base, default 60s), `NodeId` (default
machine name plus a process GUID). Due-ness and lease time are the database clock; the app
clock is used only for loop pacing.

`BatchSize <= MaxDegreeOfParallelism` is a hard invariant (F-1): the service claims only as
many rows as it can dispatch immediately, so every claimed row's heartbeat starts at claim
time and no claimed-but-queued row can have its lease lapse before dispatch. The defaults
(4 and 4) satisfy it; the service also clamps the effective batch to
`min(BatchSize, MaxDegreeOfParallelism)` so a misconfiguration cannot violate it.

### D8: Structured logging (Codex, L1)

The scheduler emits structured log fields for observability: `claimed`, `completed`,
`failed`, `dead` (a collector that went terminal after `MaxAttempts`), `lease-lost`, and
`next-due` per collector, plus the collector id and run id. This makes the no-op phase
observable and carries forward to the real runner. (The no-op runner logs each dispatch
itself, so the dispatch is observable without a separate scheduler-emitted field.)

### D9: One migration, replay-safe

Migration `016_collector_scheduler.sql` creates `collector_scheduler_state` with
`CREATE TABLE IF NOT EXISTS`, so a crash between the DDL and the version record is
re-runnable without manual recovery. There is no seed row (rows are ensured lazily by the
service, D4). No foreign key to `evidence_collectors`: the state must survive GitOps churn
or deletion of a collector, consistent with the scalar-id convention used by `evidence_runs`.

Column types (F-4, pinned so the durable tokens match the repo convention):

| column                | type                          | notes                                   |
| --------------------- | ----------------------------- | --------------------------------------- |
| `collector_id`        | `VARCHAR(190)` `utf8mb4_bin`  | primary key; scalar id convention       |
| `next_due_at`         | `DATETIME(6)` NOT NULL        | due-ness compared on DB clock           |
| `current_run_id`      | `CHAR(26)` `utf8mb4_bin` NULL | ULID from `IUlidFactory`; idempotency key |
| `lease_owner`         | `VARCHAR(190)` `utf8mb4_bin` NULL | `NodeId` of the holder             |
| `lease_token`         | `CHAR(26)` `utf8mb4_bin` NULL | ULID from `IUlidFactory`; fencing token |
| `lease_expires_at`    | `DATETIME(6)` NULL            |                                         |
| `lease_heartbeat_at`  | `DATETIME(6)` NULL            |                                         |
| `last_started_at`     | `DATETIME(6)` NULL            |                                         |
| `last_completed_at`   | `DATETIME(6)` NULL            |                                         |
| `last_success_at`     | `DATETIME(6)` NULL            |                                         |
| `last_failure_at`     | `DATETIME(6)` NULL            |                                         |
| `failure_count`       | `INT NOT NULL DEFAULT 0`      |                                         |
| `last_error`          | `TEXT` NULL                   | last failure message                    |
| `status`              | `VARCHAR(16)` `utf8mb4_bin` NOT NULL DEFAULT `'pending'` | closed set (D4) |
| `config_fingerprint`  | `CHAR(64)` `utf8mb4_bin` NULL | hash over frequency + type; drives revival |
| `created_at`          | `DATETIME(6)` NOT NULL DEFAULT `UTC_TIMESTAMP(6)` |                    |
| `updated_at`          | `DATETIME(6)` NOT NULL DEFAULT `UTC_TIMESTAMP(6)` ON UPDATE `UTC_TIMESTAMP(6)` | |

Index the claim path (at least `next_due_at`). `current_run_id` and `lease_token` are ULIDs
generated via `IUlidFactory` (not raw GUIDs), so `AddCollectorScheduler` TryAdds
`IUlidFactory` if a co-registered store has not already.

`created_at`/`updated_at` default to `UTC_TIMESTAMP(6)` (and `updated_at` uses
`ON UPDATE UTC_TIMESTAMP(6)`) so ensure's `INSERT`/`INSERT ... ON DUPLICATE KEY UPDATE`
cannot fail on these NOT NULL columns without the caller supplying them (F-9).

### D10 (mediator): Terminal dead-letter is a status on the one row, not a separate queue

A persistently failing collector must stop retrying and be surfaced, not retry forever. We
add a terminal `dead` status (D4): when `failure_count` reaches `MaxAttempts` (default 5) the
fenced failure `UPDATE` sets `status='dead'` and the claim query excludes dead rows, so the
collector is not claimed again until it is reset (manually) or revived by a config/frequency
change (ensure detects a changed `config_fingerprint`). The failure is recorded on the same
row (`last_error`, `last_failure_at`, `failure_count`, `status`) for observability.

We deliberately do NOT add a separate `job_queue` or `dead_letter_queue` table and do NOT
introduce a producer/consumer split. `dead` is just a terminal status on the existing
`collector_scheduler_state` row; the model stays one table.

Why keep the specialized per-collector model rather than a generic job queue:

- This is a recurring cadence, not one-shot jobs. Each collector has exactly one durable
  schedule row advanced by `next_due_at`; there is no stream of distinct work items to
  enqueue. A generic queue models one-shot jobs and would need a producer that periodically
  enqueues "run collector X now" items.
- That producer would itself need cross-replica dedup (every replica would race to enqueue
  the same due collector), reintroducing exactly the single-writer/leader coordination this
  design avoided with per-collector `SELECT ... FOR UPDATE SKIP LOCKED`. The one-row model
  makes the schedule, the in-flight run, the lease, and the terminal state a single fenced
  unit with no separate producer to coordinate.
- Missed windows must collapse into a single catch-up run (D4). A queue of enqueued jobs
  naturally accumulates one item per missed window unless the producer dedupes; the single
  `next_due_at` timestamp collapses them for free.

A generic queue would be more code, a new table, a new producer, and its own dedup - all to
express something the one-row cadence model already handles. Dead-lettering does not require
any of it.

## Risks / Trade-offs

- Lease-handover double-dispatch (at-least-once). A paused worker resuming after its lease
  expired could dispatch a collector a new holder also dispatched. Mitigation: heartbeat at
  ~TTL/3 with cooperative cancellation (D5), fenced completion by `lease_token` (D2), a
  stable `current_run_id` the future runner keys idempotent appends on. Accepted as bounded
  at-least-once with an idempotent downstream.
- `FOR UPDATE SKIP LOCKED` requires InnoDB and MySQL 8.0+. The project targets MySQL 8.4, so
  this is satisfied; noted as a hard dependency.
- A crashed worker's row stays leased until `lease_expires_at`, so a collector's next run is
  delayed by at most one TTL. Acceptable; the TTL bounds the delay.
- Stale `collector_scheduler_state` rows for deleted or type-changed collectors are never
  claimed (excluded by the active-collector filter, D1) but linger. Harmless; pruning is out
  of scope.
- A collector that fails `MaxAttempts` times goes `dead` and stops retrying until reset or a
  config change (D4/D10). This trades automatic recovery for bounded failure cost and clear
  surfacing; a transient outage lasting longer than `MaxAttempts` retries needs a manual
  reset or a config touch to resume. Accepted: `MaxAttempts` defaults to 5 and the state is
  visible via `status`/`last_error`.
- The deferred H1 (unit of work) and M3 (vendorless integration) concerns mean the no-op
  scheduler cannot become a real runner without the follow-up change resolving evidence
  attribution. Explicitly deferred, not hidden.
- Backoff on a failing integration spaces retries out up to the interval cap
  (`min(interval, BaseBackoff * 2^failure_count)`); after `MaxAttempts` the collector goes
  `dead` and stops. A collector failing or dead is visible via `status`/`failure_count`/
  `last_error`. Acceptable.
- Every replica runs the service and competes to claim; SKIP LOCKED keeps this contention
  cheap (claims are disjoint, no blocking). Acceptable overhead.

## Migration Plan

1. Ship migration `016_collector_scheduler.sql`; operators apply it with the CLI
   (`freeboard system migrate`). It creates one table and is replay-safe.
2. Deploy the web app. Each replica starts the scheduler, ensures state rows for integration
   collectors, and claims due ones. With the no-op runner the observable effect is claim /
   complete log lines and `next_due_at` advancing; real collection lands when the runner
   change ships.
3. If migration 016 is not yet applied, the scheduler logs a clear warning and backs off; the
   app still serves requests. Disable at runtime with
   `Freeboard:CollectorScheduler:Enabled=false` (the service then no-ops without touching the
   database). Migrations are forward-only; the additive table is ignored by older code, so no
   down-migration is authored.

## Open Questions

- Should due-ness fire at the full cadence interval, or a fraction so evidence refreshes
  before it can cross interval + grace? Chosen: the full interval (D3/D4). Confirm.
- H1 and M3 are deferred to the runner-execution change; reviewers should confirm they are
  acceptable to defer and that the persisted `current_run_id` and control-attach model give
  that change enough to resolve evidence attribution safely.
- Should a graceful shutdown proactively release in-flight leases (faster failover) or leave
  them to expire (simpler, at most one TTL delay)? Chosen: leave to expire. Confirm.
- `MaxAttempts` default (5) and revival policy: revival on manual reset or a
  `config_fingerprint` change is proposed. Confirm this is the right reset surface, and
  whether a dead collector should also surface anywhere beyond logs/`status` (for example a
  future admin view) - out of scope here but reviewers should note it.
