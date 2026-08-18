## MODIFIED Requirements

### Requirement: Unresolvable token warns at startup and fails collection as a scheduler error

The system SHALL warn once at startup for each integration-connection that is
referenced by a `Collector` of `type: integration` and whose token is not
resolvable, scanning the one unified collector set. The warning SHALL name the connection
id and SHALL NOT include the
token value. An unresolvable token SHALL NOT be a boot gate: the application SHALL
start regardless.

At collection time, a collector whose connection has no resolvable token SHALL fail
its scheduled dispatch rather than silently succeeding or crashing the scheduler. The
failure SHALL be recorded as the collector-scheduler's per-collector `error` status
(the existing failed-dispatch status set by the scheduler). It SHALL NOT be recorded as a
`Pass` or a `Fail` evidence run: a `Pass` is a false green and a `Fail` asserts an observed
policy failure that no collection observed.

The evidence run `result` set is `{Pass, Fail, Error}`. The per-check `result` set stays
closed to `{Pass, Fail}`. An unresolvable token MAY therefore also be recorded as an
`Error` evidence run, which the derived per-collector status reports as `Errored` rather
than as a false green. Whether the runner appends such a run in addition to setting the
scheduler's `error` status is a decision for the runner that performs integration
execution. This requirement fixes only the two rules that hold either way: the scheduler
`error` status is always set, and the failure is NEVER recorded as a `Pass` or a `Fail`
run.

If the runner does append such a run, it SHALL do so only for a failure that ends the cycle's
attempt, never for a failure it will retry under the same cycle id. An appended run is
final for its cycle, so a retry that later resolves the token could not replace an `Error`
already recorded for that cycle. An unresolvable token normally qualifies, because a retry
against unchanged configuration repeats the same failure.

An appended `Error` run SHALL carry an error detail, because the store requires one. The
detail SHALL name the connection whose token could not be resolved. The token value SHALL
NOT appear in the recorded failure, in an appended run's error detail, or in any log. Building the runner that fails the dispatch is out of scope for this
change; this requirement fixes the contract the runner SHALL honour.

#### Scenario: Referenced connection with no token warns at startup

- **WHEN** the application starts and a referenced integration-connection has no
  resolvable token
- **THEN** a startup warning naming the connection id is logged, the token value is
  not logged, and the application boots

#### Scenario: Collector with an unresolvable token fails as a scheduler error

- **WHEN** a collector attached to a connection whose token is unresolvable is
  collected
- **THEN** its scheduled dispatch fails and is recorded as the scheduler's `error`
  status, not as a `Pass` or `Fail` evidence run, and the token value appears in no log
  or stored field

#### Scenario: A recorded token failure is never a passing verdict

- **WHEN** a dispatch fails on an unresolvable token and the runner appends an evidence
  run for it
- **THEN** that run's result is `Error`, the derived per-collector status is `Errored`,
  and the token value appears in neither the run's error detail nor any log
