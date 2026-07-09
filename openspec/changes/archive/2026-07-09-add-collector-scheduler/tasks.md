## 1. Core interval helper

Commit: `feat(core): add public collection-interval helper to frequency policy`

- [x] 1.1 Add a public `Interval(string? frequency)` to
  `src/Freeboard.Core/GitOps/EvidenceCollectorFrequency.cs` returning the cadence window
  (`continuous` 1h ... `annual` 366d) or null for null/blank/unknown. Reuse the existing
  `Cadences` map; add no new token set. Leave `IsStale` (interval + grace) unchanged.
- [x] 1.2 Add unit tests in `tests/Freeboard.Core.Tests`: each known token maps to its window;
  `continuous` is 1h; null/blank/unknown returns null. Assert the interval is the window
  only, distinct from the interval-plus-grace staleness threshold.
- [x] 1.3 Verify: `dotnet build` and `dotnet test tests/Freeboard.Core.Tests`.

## 2. Persistence: scheduler-state table, store, and DI

Commit: `feat(persistence): add per-collector scheduler-state store and migration`

- [x] 2.1 Add migration `src/Freeboard.Persistence/Migrations/016_collector_scheduler.sql`
  creating `collector_scheduler_state`. Pin every column type (F-4):
  - `collector_id` PK `VARCHAR(190)` `utf8mb4_bin`.
  - `next_due_at DATETIME(6)` NOT NULL.
  - `current_run_id CHAR(26)` `utf8mb4_bin` NULL - ULID (the future idempotency key).
  - `lease_owner VARCHAR(190)` `utf8mb4_bin` NULL - the holder `NodeId`.
  - `lease_token CHAR(26)` `utf8mb4_bin` NULL - ULID fencing token.
  - `lease_expires_at` / `lease_heartbeat_at DATETIME(6)` NULL.
  - history `last_started_at` / `last_completed_at` / `last_success_at` / `last_failure_at`
    (all nullable `DATETIME(6)`).
  - `failure_count INT NOT NULL DEFAULT 0`.
  - `last_error TEXT` NULL.
  - `status VARCHAR(16)` `utf8mb4_bin` NOT NULL DEFAULT `'pending'` - closed set
    `pending|running|ok|error|dead` (replaces the earlier `last_status`).
  - `config_fingerprint CHAR(64)` `utf8mb4_bin` NULL - hash over frequency + type; drives
    dead/error revival on config change.
  - `created_at DATETIME(6)` NOT NULL DEFAULT `UTC_TIMESTAMP(6)`.
  - `updated_at DATETIME(6)` NOT NULL DEFAULT `UTC_TIMESTAMP(6)` ON UPDATE `UTC_TIMESTAMP(6)`
    (so ensure's INSERT/upsert cannot fail on the NOT NULL timestamps, F-9).
  Index the claim path (at least `next_due_at`). No foreign key to `evidence_collectors`. Use
  `CREATE TABLE IF NOT EXISTS` (replay-safe); no seed row. Include a header explaining the
  design and replay-safety.
- [x] 2.2 Add `ICollectorSchedulerStore` and `MySqlCollectorSchedulerStore`. Define the
  claim return type `ClaimedCollectorLease(string CollectorId, string LeaseToken, string
  CurrentRunId, DateTime LeaseExpiresAt)` - the claim must return these per claimed row
  because `lease_token` is minted and `current_run_id` is resolved (`COALESCE`) inside the
  claim `UPDATE`, so the caller cannot reconstruct them and the fencing lifecycle needs them
  (F-2). Methods:
  - `EnsureScheduledAsync(items)` where each item carries `(collectorId, configFingerprint)`:
    one `INSERT ... ON DUPLICATE KEY UPDATE` in the MySQL 8.0.20+ aliased-row form
    (`VALUES (...) AS new ON DUPLICATE KEY UPDATE col = new.col`, not the deprecated
    `VALUES()`; target is MySQL 8.4). Seed a missing row `status='pending'`, `failure_count=0`,
    `next_due_at = UTC_TIMESTAMP(6)`, storing the fingerprint. On an existing row: `running` ->
    untouched; `pending`/`ok` -> refresh the stored `config_fingerprint` when it differs, no
    other change (F-12b: a stale fingerprint on a live row otherwise gets wrongly re-revived
    every cycle and never reaches `dead`); `error`/`dead` with a differing fingerprint ->
    refresh the fingerprint AND revive (`status='pending'`, `failure_count=0`,
    `last_error=NULL`, and for a revived `dead` row `next_due_at = UTC_TIMESTAMP(6)`). The SET
    list MUST assign `config_fingerprint` LAST (and gate every revived column on the pre-update
    `status` and `config_fingerprint`), because MySQL evaluates SET left-to-right: assigning
    the fingerprint first makes `config_fingerprint <> new.config_fingerprint` compare the row
    to itself and revival never fires (F-12a).
  - `ClaimDueAsync(owner, ttl, batchSize, activeCollectorIds)`: short transaction, `SELECT ...
    WHERE next_due_at <= UTC_TIMESTAMP(6) AND status <> 'dead' AND collector_id IN
    @activeCollectorIds AND (lease_owner IS NULL OR lease_expires_at <= UTC_TIMESTAMP(6))
    ORDER BY next_due_at LIMIT @batch FOR UPDATE SKIP LOCKED`; for each row mint a ULID
    `lease_token` and ULID `newRunId` via `IUlidFactory`, set lease
    owner/token/expiry/heartbeat, `status='running'`, `last_started_at`, and `current_run_id =
    COALESCE(current_run_id, @newRunId)`; then read back the effective row and return
    `ClaimedCollectorLease` per claim. Empty `activeCollectorIds` claims nothing.
  - `RenewLeaseAsync(id, token, ttl)` (fenced, returns whether still held).
  - `ReleaseLeaseAsync(id, token, status, nextDueAt?)` (fenced; clears the lease columns, sets
    `status`, and `next_due_at = COALESCE(@nextDueAt, next_due_at)` without recording a run
    outcome). This is the release path for the null-interval skip (F-6), which cannot use
    `CompleteFailureAsync` because that requires a non-null interval. Leaves `current_run_id`
    as-is (the collector was not dispatched).
  - `CompleteSuccessAsync(id, token, interval)` (fenced; `next_due_at = completion +
    interval`, `status='ok'`, clear run id and lease, reset `failure_count`, `last_error=NULL`).
  - `CompleteFailureAsync(id, token, error, interval, baseBackoff, maxAttempts)` (fenced; keep
    `current_run_id`, release lease; compute in SQL from the pre-increment `failure_count`:
    `status = IF(failure_count+1 >= maxAttempts,'dead','error')` and, when not dead,
    `next_due_at = UTC_TIMESTAMP(6) + LEAST(interval, baseBackoff * 2^failure_count)`; a dead
    row leaves `next_due_at` as-is since it is never claimed). Because MySQL evaluates the SET
    list left-to-right, `failure_count = failure_count + 1` MUST be the LAST assignment so
    `status`, `next_due_at`, and `POW(2, failure_count)` read the pre-increment value; putting
    it first would push the row to `dead` one failure early and double the backoff exponent.
    The previous precomputed `backoff` parameter is removed (F-5).
  All time on the database clock (`UTC_TIMESTAMP(6)`).
- [x] 2.3 Add `AddCollectorScheduler(connectionString)` to
  `PersistenceServiceCollectionExtensions.cs` registering the connection factory (TryAdd), the
  store, and `IUlidFactory` (TryAdd `UlidFactory`, matching the other stores) since the store
  mints ULID run/lease tokens (F-4).
- [x] 2.4 Add gated MySQL integration tests in `tests/Freeboard.Persistence.Tests` (gate on
  `FREEBOARD_TEST_DB`, skip cleanly when unset): two owners claim disjoint due rows and never
  the same one (no double-run via SKIP LOCKED); an expired lease is reclaimed by another owner
  with `current_run_id` preserved (crash reclaim); a stale lease token cannot complete or fail
  a run (fencing); `RenewLeaseAsync` extends `lease_expires_at` for the current `lease_token`
  and a renewal carrying a stale/wrong `lease_token` affects 0 rows (renewal fencing); success
  advances `next_due_at` by one interval; a collector overdue by many
  intervals runs one catch-up run; failure applies bounded backoff and increments
  `failure_count`; the off-by-one boundary is pinned with `MaxAttempts=2` - the FIRST failure
  yields `status='error'` with `next_due_at` at `BaseBackoff` from now (the `2^0` backoff, not
  `2^1`), and the SECOND failure yields `status='dead'` (proving the SET-list orders
  `failure_count = failure_count + 1` last so `status`/backoff read the pre-increment count);
  failing `MaxAttempts` times sets `status='dead'` and the row is no longer
  claimed; a `dead`/`error` row is revived when its `config_fingerprint` changes and is
  claimable again, and the revival test asserts the SET-list ordering by confirming a stored
  fingerprint that differs from the incoming one actually triggers revival (proving the
  comparison reads the pre-update fingerprint); a claim excludes collectors absent from `activeCollectorIds` (deleted or
  integration->script type-changed collectors are never leased); migration creates the table
  and is re-runnable.
- [x] 2.5 Verify: `dotnet build`; `dotnet test tests/Freeboard.Persistence.Tests` (skips gated
  cases without `FREEBOARD_TEST_DB`; bring up the MySQL test stack to exercise them).

## 3. Web: hosted scheduler service and no-op runner seam

Commit: `feat(scheduler): add in-service per-collector scheduler with lease claiming`

- [x] 3.1 Add `src/Freeboard/Scheduler/SchedulerOptions.cs` bound from
  `Freeboard:CollectorScheduler`: `Enabled` (default true), `PollInterval` (default 30s),
  `LeaseTtl` (default 90s), `MaxDegreeOfParallelism` (default 4), `BatchSize` (default 4),
  `MaxAttempts` (default 5), `BaseBackoff` (default 60s), `NodeId` (default machine name plus a
  process GUID). Enforce the invariant `BatchSize <= MaxDegreeOfParallelism` by clamping the
  effective batch to `min(BatchSize, MaxDegreeOfParallelism)` (F-1), so a claimed row is never
  left queued without a heartbeat.
- [x] 3.2 Add `src/Freeboard/Scheduler/IScheduledCollectorRunner.cs` with
  `RunAsync(EvidenceCollectorRow collector, string runId, CancellationToken)` and a default
  `LoggingScheduledCollectorRunner.cs` that logs the dispatch and returns (no evidence).
- [x] 3.3 Add `src/Freeboard/Scheduler/CollectorSchedulerService.cs` (`BackgroundService`):
  return immediately when disabled or the connection string is empty; each cycle read
  `integration` collectors (`IComplianceStore`), `EnsureScheduledAsync` (with each collector's
  `config_fingerprint`) for those with a resolvable interval only, then `ClaimDueAsync` passing
  the current integration collector ids that have a resolvable interval as `activeCollectorIds`
  (null-interval ids are excluded so such collectors are never claimed, F-13) and the clamped
  batch. For each claimed collector: resolve its interval via
  `EvidenceCollectorFrequency.Interval` and, if null (a purely defensive case), skip it and
  release the lease with `status='dead'` and a warning (F-6, F-13) so it is not re-claimed;
  otherwise start a heartbeat at ~TTL/3 (renew fenced; on lost lease cancel a
  linked `CancellationTokenSource`), dispatch through the runner with `current_run_id` and the
  linked token, then STOP and AWAIT the heartbeat loop before issuing the fenced completion
  (F-10), and call `CompleteSuccessAsync` or `CompleteFailureAsync` (on exception). Sleep the
  poll interval; on host stop cancel in-flight dispatches and leave leases to expire. Catch
  ONLY the missing-table error - `MySqlException` with `ErrorCode ==
  MySqlErrorCode.NoSuchTable` (1146) - log a clear warning, and back off; do not broadly catch
  (F-7). Emit structured log fields (claimed / completed / failed / dead / lease-lost /
  next-due, with collector id and run id).
- [x] 3.4 Wire in `src/Freeboard/Program.cs`: bind `SchedulerOptions`, call
  `AddCollectorScheduler`, `TryAddSingleton<IScheduledCollectorRunner,
  LoggingScheduledCollectorRunner>` (so a later real runner can replace it), and
  `AddHostedService<CollectorSchedulerService>`.
- [x] 3.5 Set `Freeboard:CollectorScheduler:Enabled=false` in one shared place all web test
  factories already use - `AuthTestConfig.Apply(IWebHostBuilder)` in
  `tests/Freeboard.Web.Tests/AuthTestConfig.cs` (called by `AuthWebFactory`,
  `ComplianceWebFactory`, `GitOpsWebFactory`, and the email-registration factory) - so the
  scheduler is off by default unless a test opts in. Note: the empty-connection guard already
  makes the scheduler inert in tests that do not configure a connection string; this flag is a
  belt-and-braces default (F-8).
- [x] 3.6 Add orchestration tests in `tests/Freeboard.Web.Tests` using in-memory fakes (fake
  scheduler store, fake compliance store, fake runner, parameterised clock): a due integration
  collector is claimed and dispatched once with its run id; a lost lease (heartbeat renewal
  reports not-held) cancels the in-flight dispatch via the linked `CancellationToken`;
  non-integration collectors are
  never scheduled; a runner exception is caught, the lease released and run token kept, and the
  batch continues; a collector failing `MaxAttempts` times goes `dead` and is not dispatched
  again; a collector with a null interval is NOT claimed on the next cycle - either filtered
  out of `activeCollectorIds` (not seeded, not claimed) or, if defensively claimed, released
  with `status='dead'` and not re-claimed (F-13); disabled or empty-connection scheduler
  dispatches nothing and does not query; a missing-table
  error (`MySqlErrorCode.NoSuchTable`) is logged and backed off rather than thrown while other
  errors still surface.
- [x] 3.7 Verify: `dotnet build`; `dotnet test tests/Freeboard.Web.Tests`.

## 4. Full verification

- [x] 4.1 Run `dotnet build` for the solution and `dotnet test` (default tier passes without
  `FREEBOARD_TEST_DB`; bring up the MySQL test stack and set `FREEBOARD_TEST_DB` to exercise the
  gated claim, reclaim, and fencing tests).
- [x] 4.2 Run `openspec validate "add-collector-scheduler"` and confirm the change validates.
