## MODIFIED Requirements

### Requirement: In-service scheduler claims and runs due integration collectors

The web app SHALL host an ASP.NET `BackgroundService` that periodically claims due
integration collectors and dispatches each through an `IScheduledCollectorRunner` seam. Only
collectors whose `type` is `integration` SHALL be scheduled; `script`, `agent`, `manual`,
and `training` collectors SHALL NOT be run in the ASP.NET process. Before claiming, the
service SHALL ensure a scheduler-state row exists for each integration collector, seeding a
new collector as immediately due. The service SHALL read the unified collector set through
the existing `IComplianceStore`, and the runner seam SHALL receive the merged collector read
model. The default runner SHALL be a no-op that logs the dispatch and produces no evidence;
real integration execution is out of scope for this capability.

Each claimed collector SHALL be dispatched with its stable run id (`current_run_id`) so a
future real runner can make its work idempotent on that id.

The run id SHALL identify ONE collection cycle of ONE collector, and its lifecycle SHALL be:

1. The claim SHALL mint a new ULID into `current_run_id` only when the column is null, so a
   claim never overwrites the token of a cycle that is still in progress.
2. A failed run SHALL keep the token, so the retry runs under the same cycle id.
3. A run whose runner RETURNED NORMALLY SHALL clear the token, so the NEXT claim mints a new
   one and the next cycle is a different cycle.
4. A config change that revives a `dead` or `error` collector SHALL clear the token, because
   the revived collector starts a semantically new cycle.

Rule 3 is what keeps the token usable as an idempotency key. A runner that keys an
append on the run id records one row per cycle result; if the token survived a successful
cycle, every later cycle would collide with the first and the collector would record nothing
more. The token is per collector because the scheduler state is one row per collector.

"Returned normally" means the runner's `RunAsync` returned without throwing. It does NOT
mean "did not fail". The distinction decides every path below, so the capability states it
once here.

Rule 3 SHALL hold on every path where the runner returned normally, including a dispatch
whose lease was lost mid-run and a dispatch that returned while the host was stopping. The
service SHALL therefore attempt its fenced completion on each of those paths rather than
returning early. The lease fence stays the guard: a completion whose lease token no longer
matches the row SHALL change nothing, so a worker that lost its lease cannot overwrite the
state of the holder that replaced it.

A runner that returns normally under a cancelled token is asserting that it finished its
work. The completion the service then attempts records `status = 'ok'`, clears
`current_run_id`, and advances `next_due_at` by a full interval, exactly as an uninterrupted
success does. A runner that has not finished SHALL NOT return normally under a cancelled
token. The runner interface's own documentation SHALL state this, so a runner is written
against it: honoring the token means throwing `OperationCanceledException` rather than
returning, because a normal return is read as a finished cycle. That thrown path is the one
rule 3 does not touch.

A dispatch whose runner threw while its token was cancelled SHALL record NO outcome at all.
It SHALL NOT clear the token, and it SHALL NOT record a run failure. Cancellation is the
service stopping its own work, not the collector failing. Recording it as a failure would
increment the collector's failure count, apply a backoff, and after the configured maximum
attempts move the collector to the terminal `dead` status, from which nothing but a config
change revives it. Keeping the token is the correct outcome, because the retry then reuses
the same cycle id, which is rule 2.

The completion write SHALL run under a cancellation token that host shutdown does not
cancel, so a dispatch that finished still ends its cycle. That token SHALL also carry a short
timeout rather than being uncancellable, so a stalled database connection cannot hold the
background service open through host teardown.

A lost lease therefore leaves the token in place, and the new holder claims the SAME
`current_run_id` and re-dispatches the same cycle. A runner SHALL treat a re-delivered
append under its own cycle id as an accepted replay, not as a run failure. A runner that
reported the collision as a failure would keep the token alive through every retry, and the
collector would reach `dead` without ever ending the cycle.

#### Scenario: A due integration collector is claimed and dispatched

- **WHEN** an `integration` collector's `next_due_at` is at or before the current database
  time and it is unleased
- **THEN** the scheduler claims it and dispatches it once through the runner, passing its
  stable run id

#### Scenario: Non-integration collectors are never scheduled

- **WHEN** a collector whose `type` is `agent`, `script`, `manual`, or `training` exists
- **THEN** the scheduler neither ensures a state row for it nor dispatches it

#### Scenario: A new integration collector becomes due immediately

- **WHEN** an integration collector has no scheduler-state row yet
- **THEN** the service inserts a state row with `next_due_at` set to the current time, so the
  collector is due on the next cycle

#### Scenario: A successful run ends its cycle and the next claim mints a new run id

- **WHEN** a collector's dispatch succeeds and the collector is later claimed again
- **THEN** the second claim dispatches a run id different from the first, because the
  successful completion cleared `current_run_id`, so work keyed on the run id is not treated
  as a repeat of the previous cycle

#### Scenario: A dispatch that finishes as the host stops still ends its cycle

- **WHEN** the runner returns normally and the host is stopping
- **THEN** the dispatch still records the completion and clears `current_run_id`, because
  the completion write runs under a token that shutdown does not cancel

#### Scenario: A completion by a worker that lost its lease changes nothing

- **WHEN** a dispatch whose lease was taken by another worker returns normally and attempts
  its completion
- **THEN** the fenced write matches no row, the state stays with the new holder, and
  `current_run_id` is left in place so the new holder re-dispatches the same cycle

#### Scenario: A cancelled runner records no failure and keeps its token

- **WHEN** the host stops or the lease is lost, and the runner honors its token by throwing
  `OperationCanceledException`
- **THEN** the dispatch records no completion of either kind, so the collector's status and
  failure count are unchanged and `current_run_id` is left in place for the retry

### Requirement: A failed run backs off and preserves the run token

The system SHALL isolate and record run failures. When a dispatch fails, the service SHALL
release the collector's lease, keep its `current_run_id` so a retry reuses the same stable
token, increment `failure_count`, record `last_failure_at` and `last_error`, set `status` to
`error`, and set `next_due_at` to a bounded exponential backoff from now
(`min(interval, BaseBackoff * 2^failure_count)`, base 60s by default, capped at the
collector's interval). A failure of one collector SHALL NOT prevent other due collectors in
the same cycle from being claimed and dispatched.

A dispatch fails when the RUNNER reports a failure. A dispatch the service itself cancelled -
the host stopping, or the lease moving to another worker - SHALL NOT be recorded as a run
failure, even though the runner threw, because the collector did not fail. That dispatch
records no outcome and keeps its token, as the run-token lifecycle above states.

#### Scenario: One failing collector does not block the batch

- **WHEN** the runner throws a failure of its own, with its token not cancelled, for one
  claimed collector during a cycle
- **THEN** the failure is caught and recorded, the collector's lease is released and its run
  token retained, and the other due collectors are still claimed and dispatched

#### Scenario: A failed run backs off before retrying

- **WHEN** a collector's dispatch fails and its `failure_count` is still below `MaxAttempts`
- **THEN** its `status` becomes `error`, its `next_due_at` is set to a bounded exponential
  backoff from the current time, and its `failure_count` is incremented, so it is retried
  later rather than immediately
