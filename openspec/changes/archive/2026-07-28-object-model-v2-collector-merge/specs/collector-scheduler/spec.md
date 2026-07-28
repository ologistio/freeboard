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

### Requirement: Due-ness and rescheduling derive from the collection cadence

The system SHALL compute the scheduling interval using a public helper on
`CollectorFrequency` that reuses the frequency vocabulary and per-cadence window; it
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

### Requirement: Scheduler state is durable across restart and crash

The system SHALL persist each collector's schedule, in-flight run token, lease, and run
health in a `collector_scheduler_state` row so scheduling survives process restart and
worker crash. A restarted or newly claiming worker SHALL read persisted state and SHALL NOT
re-run a collector whose `next_due_at` is still in the future. The state table SHALL have no
foreign key to the collector table - `collectors` after the collector merge - so a state row
survives GitOps churn or deletion of a collector.

#### Scenario: A restarted worker does not re-run a not-yet-due collector

- **WHEN** a collector ran recently, its `next_due_at` is in the future, and a worker restarts
- **THEN** the collector is not claimed again until `next_due_at` is reached

### Requirement: Claiming is limited to active integration collectors

The service SHALL claim only collectors that are still active integration collectors this
cycle, by passing the current integration collector ids from `IComplianceStore` as the claim
filter. A collector deleted from config, or whose `type` changed away from `integration`,
SHALL NOT be claimed even though its `collector_scheduler_state` row lingers. A change to a
collector's `provider` or `config` SHALL NOT by itself affect claiming.

#### Scenario: A deleted or type-changed collector is not claimed

- **WHEN** a `collector_scheduler_state` row exists for a collector that no longer appears as
  an `integration` collector (deleted, or its `type` changed to `script`)
- **THEN** the claim excludes it and it is never leased again, though its row is left in place

## ADDED Requirements

### Requirement: The schedule fingerprint covers every collection input

The service SHALL derive each collector's schedule fingerprint from its collection inputs:
its `type`, its `frequency`, its `provider`, its `connection`, and its config in a
canonical form. The fingerprint SHALL NOT include the collector's `threshold`, which is a
scoring input consumed at evaluation rather than a collection input, so changing it cannot
repair a failed collection.

The hashed input SHALL be those five parts in that order, joined by a newline, extending the
pre-merge two-part `type`-newline-`frequency` form rather than introducing a second layout;
an absent `provider` or `connection` SHALL contribute the empty string. The fingerprint is
persisted and compared across restarts and across upgrades, so its layout is part of the
contract and not an implementation detail.

The fingerprint exists so that a change to a collector's collection inputs revives a
scheduler-state row that has settled into the `error` or `dead` status when the ensure step
next runs. It SHALL NOT reset the `next_due_at` of a healthy row. Because an integration
collector's tracked `checks` are a `config` key, the two most likely operator repairs for a
failing collector - correcting a check's provider-native `source_key` and repointing the
`connection` at the right instance - are within the fingerprint, so an operator's fix takes
effect without a further edit to the `type` or the cadence.

The canonical form SHALL be the typed config the store already returns on the collector read
model, serialized with every member emitted in an explicitly pinned order. The pinning SHALL
cover the WHOLE serialized graph, not only the config view's own members: every record the
serialization can reach - the config view and each nested form-field, quiz-item, and check
item record - SHALL have every one of its members pinned. The nested items are where the
variable part of the input lives, because an integration collector's check list is the only
member that differs between two fingerprinted collectors, so pinning the outer members alone
would leave the varying part of a persisted comparison resting on an unspecified order. The
order SHALL
be pinned by a per-member serialization attribute rather than left to the serializer's
reflection order, which is unspecified: because the fingerprint is persisted and compared
across upgrades, an emission order that changed between builds would change the comparison
and revive every dead or errored row once. The service SHALL NOT read the raw stored config
text
and SHALL NOT require a config member on the read model beyond the one the read surfaces
already use. Because only `type: integration` collectors are fingerprinted and an
integration collector's whole registered config is its `checks`, the redaction the read
model applies to a training quiz removes nothing the fingerprint needs, and no confidential
authoring data reaches the scheduler.

The fingerprint's serialization is deliberately NOT the API response projection and NOT the
storage shape, both of which omit an absent member while this one emits all of them. A hash
input is not a document: nothing reads it, so omitting an absent member buys nothing, and
binding the fingerprint to the response projection would let a later cosmetic change to that
response re-fingerprint every collector and revive every dead row once. The difference
cannot produce a collision in this increment, because the only fingerprinted collectors are
integration collectors and the only key their schema registers is `checks`, so every other
config member is always absent and contributes the same constant text to every collector's
hash; the whole variable part is the ordered check list, whose items carry the
`source_key`, `name`, and `severity` that a repair would change. That reasoning is stated
because it stops holding the moment a second key is registered for an integration pair.

#### Scenario: A config, provider, or connection edit revives a dead collector

- **WHEN** an integration collector's scheduler-state row is in `error` or `dead` status and
  its `config` (including its tracked `checks`), its `provider`, or its `connection` changes
- **THEN** its fingerprint changes and the next ensure revives the row so the collector is
  scheduled again

#### Scenario: Correcting a check's source key changes the fingerprint

- **WHEN** an integration collector's `config` changes only in one tracked check's
  `source_key`, with its `type`, `frequency`, `provider`, `connection`, and every other
  check unchanged
- **THEN** its fingerprint changes, because the check list is inside the fingerprint and
  each check item's own members are emitted in a pinned order - this
  is the operator repair the widened scope exists to make effective

#### Scenario: The fingerprint is stable across processes for an unchanged collector

- **WHEN** the same collector is fingerprinted in two separate processes with no change to
  its `type`, `frequency`, `provider`, `connection`, or `config`
- **THEN** both produce the same fingerprint, so a restart or an upgrade does not by itself
  revive a dead or errored row

#### Scenario: A threshold edit does not change the fingerprint

- **WHEN** an integration collector's `threshold` changes while its `type`, `frequency`,
  `provider`, `connection`, and `config` are unchanged
- **THEN** its fingerprint is unchanged and no dead or errored row is revived

#### Scenario: A healthy collector's schedule is not reset by a config edit

- **WHEN** an integration collector whose scheduler-state row is healthy has its `config`
  changed
- **THEN** its `next_due_at` is not reset and its cadence continues unchanged
