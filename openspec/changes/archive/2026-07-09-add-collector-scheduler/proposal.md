## Why

Evidence collectors declare a collection `frequency`, but nothing in the service acts on
it: integration-type collectors are never run on a cadence. The staleness work can flag
that a collector has gone silent, but the service does not close the loop by actually
driving collection on schedule. A production deploy runs several web replicas behind a
load balancer, so any scheduler must run each due collector once across the fleet, not once
per replica, and must isolate failure so one slow or stuck collector never blocks the
others.

This change adds an in-service scheduler that claims and runs due integration collectors on
their `frequency`. Each due collector is claimed independently with a short
`SELECT ... FOR UPDATE SKIP LOCKED` transaction and a per-collector lease, so two replicas
never run the same collector and a stuck collector holds only its own row. The schema is
crash-safe from the start: a stable run token and a lease fencing token let the future real
runner retry safely after a crash or lease expiry without double-completing.

## What Changes

- Add an ASP.NET hosted `BackgroundService` in the web app that periodically claims due
  integration collectors and dispatches each through a runner seam.
- Claim collectors PER COLLECTOR (not a single global leader): a short transaction selects
  due, unleased, non-dead rows that are still active integration collectors with `FOR UPDATE
  SKIP LOCKED`, sets a lease (owner, ULID token, expiry, heartbeat) and a stable ULID run id,
  and commits, returning the effective lease/run-id per claimed row. Failure is isolated to
  one row.
- Add one mutable scheduler-state row per collector holding due time, the in-flight run
  token, the lease, an explicit `status` (`pending`/`running`/`ok`/`error`/`dead`), and run
  history/health fields. A collector that fails `MaxAttempts` times (default 5) transitions to
  a terminal `dead` status: it is not claimed again until reset or a config/frequency change,
  and its failure is recorded (`last_error`, `last_failure_at`). No separate job or
  dead-letter queue table is introduced; `dead` is a status on the one row.
- Renew the lease on a heartbeat (~TTL/3) and cooperatively cancel the in-flight dispatch
  when the lease is lost, so a superseded worker stops promptly.
- Expose a public base-interval helper on Core `EvidenceCollectorFrequency` (the interval,
  separate from the staleness grace). Scheduling uses the interval; staleness keeps interval
  plus grace. No duplicate cadence vocabulary.
- Add a runner seam (`IScheduledCollectorRunner`) with a default no-op logging
  implementation. Real integration execution (resolving targets, appending real evidence) is
  explicitly deferred to a follow-up change.
- Add a forward-only migration creating the `collector_scheduler_state` table. Migrations
  stay CLI-driven; the web app does not auto-migrate. The scheduler logs and backs off
  clearly when its table is missing, and the app still boots with an empty connection string.

This is MIT (default). The scheduler, the per-collector lease, and the schedule state are
general platform capabilities, not paid enterprise features, so they live in
`Freeboard.Core` (the pure interval helper), `Freeboard.Persistence` (the scheduler-state
store and migration), and the web app `Freeboard` (the hosted service and the no-op runner
seam). Nothing goes in `src/Freeboard.Enterprise`. Agent and CLI are untouched and stay
EE-free and cross-platform.

## Capabilities

### New Capabilities

- `collector-scheduler`: an in-service hosted background service that computes which
  integration collectors are due from their collection cadence and durable due time,
  dispatches each due collector through a no-op runner seam, advances the due time on
  success and backs off on failure, and records run health. Missed windows collapse into a
  single catch-up run.
- `scheduler-lease`: per-collector claiming and leasing in MySQL. A short
  `SELECT ... FOR UPDATE SKIP LOCKED` transaction claims due, unleased collector rows and
  sets a lease with a fencing token; the lease is renewed on a heartbeat, is reclaimed by
  expiry after a crash, and fences completion so only the current lease holder can finish or
  reschedule a run.

### Modified Capabilities

<!-- none -->

## Impact

- Affected code (all MIT):
  - `src/Freeboard.Core/GitOps/EvidenceCollectorFrequency.cs`: add a public
    `Interval(frequency)` helper reusing the existing cadence map; no new token set. Stale
    evaluation keeps interval plus grace.
  - `src/Freeboard.Persistence/Migrations/016_collector_scheduler.sql`: new migration
    creating `collector_scheduler_state`. Replay-safe (`CREATE TABLE IF NOT EXISTS`).
  - `src/Freeboard.Persistence/ICollectorSchedulerStore.cs`,
    `MySqlCollectorSchedulerStore.cs`: ensure state rows, claim due collectors
    (`FOR UPDATE SKIP LOCKED`), renew lease, complete (fenced), fail (fenced, backoff), all
    on the database clock.
  - `src/Freeboard.Persistence/PersistenceServiceCollectionExtensions.cs`: new
    `AddCollectorScheduler` registration.
  - `src/Freeboard/Scheduler/CollectorSchedulerService.cs` (BackgroundService),
    `IScheduledCollectorRunner.cs` + `LoggingScheduledCollectorRunner.cs` (no-op seam),
    `SchedulerOptions.cs` (config). Reads collectors through the registered `IComplianceStore`.
  - `src/Freeboard/Program.cs`: bind `SchedulerOptions`, register the store, the runner, and
    `AddHostedService<CollectorSchedulerService>`.
- Affected tests:
  - `tests/Freeboard.Core.Tests`: interval-helper unit tests (clock-free).
  - `tests/Freeboard.Persistence.Tests`: gated MySQL integration tests for claiming
    (SKIP LOCKED isolation), expiry reclaim, fencing, backoff, and catch-up; skip cleanly
    without `FREEBOARD_TEST_DB`.
  - `tests/Freeboard.Web.Tests`: scheduler orchestration tests using in-memory fakes; the
    web test factories set `Freeboard:CollectorScheduler:Enabled=false` by default so the
    scheduler does not run unless a test is explicitly about scheduling.
- No new runtime dependency. No EE code. No change to the reference graph: the web app
  already references Core and Persistence; Agent and CLI are not touched.

## Non-goals

- No real integration execution. This change dispatches to a no-op logging runner; the
  concrete runners (resolving targets, calling external systems, appending real evidence
  runs) are a separate follow-up change. Two known concerns are deferred to that change and
  documented in the design, not solved here: the unit-of-work mismatch (a collector attaches
  to a control, not directly to an organisation or requirement) and vendorless integration
  collectors that cannot append evidence.
- No scheduling of non-integration collectors. Only `type == "integration"` collectors are
  scheduled; script, agent, and attestation collectors are not run in the ASP.NET process.
- No single global leader or cross-replica work coordination beyond per-collector claiming.
- No exactly-once dispatch with a monotonic fencing counter across a distributed store. The
  lease token fences completion within the current claim; a rare duplicated dispatch during a
  lease handover is tolerated because the future downstream (evidence append) is idempotent.
- No cron or calendar-aligned scheduling. Due-ness is interval-from-completion, not
  wall-clock alignment.
- No web app auto-migration. Migrations remain CLI-driven.
- No user-facing scheduler UI beyond configuration options.
- Not an EE feature; nothing moves into `src/Freeboard.Enterprise`.
