## MODIFIED Requirements

### Requirement: Lease renewal keeps a long run's claim alive

The worker SHALL renew the lease on a heartbeat while a dispatch is in flight, extending the
expiry, fenced on the lease token. A renewal that affects no rows SHALL signal that the lease
was lost, and the worker SHALL cooperatively cancel the in-flight dispatch.

A worker that lost its lease MAY still attempt its fenced completion write after the runner
returns. The fence is what makes the attempt harmless. The write names the worker's own lease
token, the row now carries the new holder's token, so the write matches no row and records no
run outcome. The worker therefore neither completes the run nor overwrites the state of the
holder that replaced it, which is what "cancels the in-flight dispatch instead of completing
it" requires.

The attempt is not optional detail. The collector scheduler's run token is an idempotency key
for the work a runner appends, and the completion write is what ends a collection cycle. A
worker that skipped the attempt whenever its linked token was cancelled would also skip it
after a plain host shutdown, which is not a lost lease at all. Attempting the write on both
paths and letting the fence decide keeps one rule instead of two.

A dispatch whose runner threw because the cancellation reached it SHALL NOT record a run
failure. Cancellation is not a collector fault, and recording it as one would count against
the collector's failure budget and drive it toward the terminal `dead` status.

#### Scenario: Heartbeat extends the lease expiry

- **WHEN** the current holder renews its lease during a run
- **THEN** the lease expiry is extended and the run continues

#### Scenario: A lost lease cancels the in-flight dispatch

- **WHEN** a heartbeat renewal affects no rows because the lease was reclaimed
- **THEN** the worker cancels the in-flight dispatch instead of completing it, and any
  fenced completion the worker attempts afterwards matches no row, so no run outcome is
  recorded and the new holder's state is untouched
