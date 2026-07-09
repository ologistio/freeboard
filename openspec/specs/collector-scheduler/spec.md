# collector-scheduler Specification

## Purpose
TBD - created by archiving change add-collector-scheduler. Update Purpose after archive.
## Requirements
### Requirement: In-service scheduler claims and runs due integration collectors

The web app SHALL host an ASP.NET `BackgroundService` that periodically claims due
integration collectors and dispatches each through an `IScheduledCollectorRunner` seam. Only
collectors whose `type` is `integration` SHALL be scheduled; script, agent, and attestation
collectors SHALL NOT be run in the ASP.NET process. Before claiming, the service SHALL
ensure a scheduler-state row exists for each integration collector, seeding a new collector
as immediately due. The service SHALL read collectors through the existing `IComplianceStore`.
The default runner SHALL be a no-op that logs the dispatch and produces no evidence; real
integration execution is out of scope for this capability.

Each claimed collector SHALL be dispatched with its stable run id (`current_run_id`) so a
future real runner can make its work idempotent on that id.

#### Scenario: A due integration collector is claimed and dispatched

- **WHEN** an `integration` collector's `next_due_at` is at or before the current database
  time and it is unleased
- **THEN** the scheduler claims it and dispatches it once through the runner, passing its
  stable run id

#### Scenario: Non-integration collectors are never scheduled

- **WHEN** a collector whose `type` is `agent`, `script`, `manual-attestation`, or
  `training-attestation` exists
- **THEN** the scheduler neither ensures a state row for it nor dispatches it

#### Scenario: A new integration collector becomes due immediately

- **WHEN** an integration collector has no scheduler-state row yet
- **THEN** the service inserts a state row with `next_due_at` set to the current time, so the
  collector is due on the next cycle

### Requirement: Due-ness and rescheduling derive from the collection cadence

The system SHALL compute the scheduling interval using a public helper on
`EvidenceCollectorFrequency` that reuses the frequency vocabulary and per-cadence window; it
SHALL NOT introduce a second cadence vocabulary. The interval per token SHALL be that token's
window: `continuous` 1 hour, `daily` 1 day, `weekly` 7 days, `monthly` 31 days, `quarterly`
92 days, `annual` 366 days. This interval is separate from the staleness grace: scheduling
uses the interval, while stale evaluation continues to use interval plus grace. A collector
whose cadence is null, blank, or not a known token SHALL yield no interval.

A collector SHALL be due when its `next_due_at` is at or before the current database time. On
a successful run the service SHALL set `next_due_at = completion_time + interval`. On a
failed run the service SHALL set `next_due_at` using a bounded retry backoff. A collector
overdue by more than one interval SHALL run exactly ONE catch-up run and then be scheduled
one interval after that run, not once per missed interval.

#### Scenario: A successful run schedules the next run one interval out

- **WHEN** a `daily` collector completes successfully at time T
- **THEN** its `next_due_at` becomes T plus 1 day

#### Scenario: Missed windows collapse into a single catch-up run

- **WHEN** a `daily` collector is overdue by five days
- **THEN** it runs once, and its next run is scheduled one day after that catch-up run, not
  five separate runs

#### Scenario: Scheduling uses the interval, not interval plus grace

- **WHEN** the service computes the next due time for a cadence token
- **THEN** it uses the plain interval (the window), while stale evaluation elsewhere
  continues to use the interval plus grace

### Requirement: A failed run backs off and preserves the run token

The system SHALL isolate and record run failures. When a dispatch fails, the service SHALL
release the collector's lease, keep its `current_run_id` so a retry reuses the same stable
token, increment `failure_count`, record `last_failure_at` and `last_error`, set `status` to
`error`, and set `next_due_at` to a bounded exponential backoff from now
(`min(interval, BaseBackoff * 2^failure_count)`, base 60s by default, capped at the
collector's interval). A failure of one collector SHALL NOT prevent other due collectors in
the same cycle from being claimed and dispatched.

#### Scenario: One failing collector does not block the batch

- **WHEN** the runner throws for one claimed collector during a cycle
- **THEN** the failure is caught and recorded, the collector's lease is released and its run
  token retained, and the other due collectors are still claimed and dispatched

#### Scenario: A failed run backs off before retrying

- **WHEN** a collector's dispatch fails and its `failure_count` is still below `MaxAttempts`
- **THEN** its `status` becomes `error`, its `next_due_at` is set to a bounded exponential
  backoff from the current time, and its `failure_count` is incremented, so it is retried
  later rather than immediately

### Requirement: A persistently failing collector goes dead and stops retrying

The system SHALL track each collector's `status` in a closed set - `pending` (waiting for
`next_due_at`), `running` (leased and dispatching), `ok` (last run succeeded), `error` (last
run failed, will retry), and `dead` (gave up) - and SHALL stop retrying a collector that
fails `MaxAttempts` times (default 5) by setting its `status` to `dead`. A `dead` collector
SHALL NOT be claimed again until it is reset or its scheduling-relevant config (for example
its `frequency`) changes, at which point the service SHALL revive it to `pending` with
`failure_count` reset. The failure SHALL remain visible via `status`, `last_error`, and
`last_failure_at`.

The ensure pass SHALL refresh the stored `config_fingerprint` on EVERY non-`running` row
whose fingerprint differs from the current config, not only on revived `dead`/`error` rows. A
`pending` or `ok` row whose config changed SHALL have its fingerprint updated with no other
change, so a later failure is not repeatedly re-revived by a stale fingerprint and can reach
`dead`. A `running` row SHALL be left untouched.

#### Scenario: A collector that keeps failing gives up

- **WHEN** a collector's dispatch fails and the incremented `failure_count` reaches
  `MaxAttempts`
- **THEN** its `status` is set to `dead`, it is no longer claimed on later cycles, and its
  `last_error` and `last_failure_at` record the terminal failure

#### Scenario: A config change revives a dead collector

- **WHEN** a collector whose `status` is `dead` has its scheduling-relevant config changed so
  its stored `config_fingerprint` no longer matches
- **THEN** the next ensure pass revives it to `status='pending'` with `failure_count` reset,
  and it is claimable again

#### Scenario: A live collector's changed config refreshes its stored fingerprint

- **WHEN** an `ok` or `pending` collector's scheduling-relevant config changes so its stored
  `config_fingerprint` no longer matches
- **THEN** the next ensure pass updates the stored `config_fingerprint` and makes no other
  change, so a later failure is not repeatedly reset by a stale fingerprint

### Requirement: Claiming is limited to active integration collectors

The service SHALL claim only collectors that are still active integration collectors this
cycle, by passing the current integration collector ids from `IComplianceStore` as the claim
filter. A collector deleted from config, or whose `type` changed away from `integration`,
SHALL NOT be claimed even though its `collector_scheduler_state` row lingers.

#### Scenario: A deleted or type-changed collector is not claimed

- **WHEN** a `collector_scheduler_state` row exists for a collector that no longer appears as
  an `integration` collector (deleted, or its `type` changed to `script`)
- **THEN** the claim excludes it and it is never leased again, though its row is left in place

### Requirement: A collector with no resolvable interval is skipped, not looped

The service SHALL guard against a null scheduling interval defensively even though
`frequency` is non-null for validated config. A null-interval collector SHALL be kept out of
claiming in the first place: the ensure pass SHALL NOT seed a state row for it and the service
SHALL exclude its id from the active-collector set passed to the claim. If a collector is
nonetheless claimed and its interval resolves to null, the service SHALL skip it - logging and
releasing its lease with a non-claimable `dead` status (excluded from future claims, revived
only by a config/frequency change or a manual reset) - rather than completing it with a null
interval, or leaving it in a claimable status with a past `next_due_at` that the claim query
would re-select every poll interval.

#### Scenario: A null-interval collector is not claimed in the first place

- **WHEN** a collector's cadence yields no interval at ensure time
- **THEN** the service does not seed a state row for it and excludes its id from the
  active-collector set, so it is never claimed

#### Scenario: A defensively claimed null interval releases into a non-claimable status

- **WHEN** a claimed collector's cadence yields no interval
- **THEN** the service releases its lease with `status='dead'`, logs the skip, does not
  dispatch or reschedule it on a zero interval, and does not re-claim it on the next cycle
  until a config change or manual reset revives it

### Requirement: Scheduler state is durable across restart and crash

The system SHALL persist each collector's schedule, in-flight run token, lease, and run
health in a `collector_scheduler_state` row so scheduling survives process restart and
worker crash. A restarted or newly claiming worker SHALL read persisted state and SHALL NOT
re-run a collector whose `next_due_at` is still in the future. The state table SHALL have no
foreign key to `evidence_collectors`, so a state row survives GitOps churn or deletion of a
collector.

#### Scenario: A restarted worker does not re-run a not-yet-due collector

- **WHEN** a collector ran recently, its `next_due_at` is in the future, and a worker restarts
- **THEN** the collector is not claimed again until `next_due_at` is reached

### Requirement: Migrations are CLI-driven and the scheduler degrades gracefully

The web app SHALL NOT run database migrations. When the `collector_scheduler_state` table is
absent (migration 016 not yet applied), the scheduler SHALL detect the specific missing-table
error (`MySqlException` with `ErrorCode == MySqlErrorCode.NoSuchTable`, MySQL 1146), log a
clear warning, and back off rather than crashing the loop or the application; it SHALL NOT use
a broad catch that would swallow transient connection or timeout errors. The app SHALL still
boot with an empty connection string (the scheduler no-ops in that case). The scheduler SHALL
be disabled by setting `Freeboard:CollectorScheduler:Enabled` to false, in which case it does
not touch the database.

#### Scenario: The app boots when the scheduler table is missing

- **WHEN** the app starts against a database where migration 016 has not been applied
- **THEN** the app boots and serves requests, and the scheduler detects the missing-table
  error specifically, logs a clear warning, and backs off instead of throwing

#### Scenario: A transient database error is not mistaken for a missing table

- **WHEN** the claim query fails with a connection or timeout error rather than
  `MySqlErrorCode.NoSuchTable`
- **THEN** the scheduler does not treat it as "table missing"; the error surfaces to the
  loop's normal error handling instead of being silently swallowed

#### Scenario: The scheduler no-ops when disabled or unconfigured

- **WHEN** `Freeboard:CollectorScheduler:Enabled` is false or the connection string is empty
- **THEN** the scheduler returns immediately without querying the database

### Requirement: Scheduler emits structured run logs

The scheduler SHALL emit structured log fields for observability, including at least:
collectors `claimed`, `completed`, `failed`, `dead` (a collector that went terminal after
`MaxAttempts`), `lease-lost`, and each collector's `next-due` time, alongside the collector
id and run id.

#### Scenario: A dispatch is logged with structured fields

- **WHEN** the scheduler claims and dispatches a collector
- **THEN** it logs structured fields identifying the collector, its run id, and the claim /
  completion outcome

### Requirement: Scheduler orchestration is tested with in-memory fakes

The scheduler's orchestration MUST be tested in the always-on test tier using in-memory
fakes (a fake scheduler store, a fake compliance store, and a fake runner), covering due-set
selection, claim, dispatch, and failure isolation with no dependency on MySQL. The web test
factories SHALL set `Freeboard:CollectorScheduler:Enabled=false` by default so the scheduler
does not run unless a test is explicitly about scheduling. The pure interval helper SHALL be
unit-tested in `Freeboard.Core.Tests` without a database or a clock.

#### Scenario: Orchestration tests run without a database

- **WHEN** `dotnet test` runs with `FREEBOARD_TEST_DB` unset
- **THEN** the scheduler orchestration tests and the interval-helper unit tests still run and
  pass, because they use fakes and a parameterised clock rather than a real database

#### Scenario: The scheduler is off by default in the web test factories

- **WHEN** a web test boots the app through the shared test factory without opting into
  scheduling
- **THEN** the scheduler is disabled and does not run background cycles

