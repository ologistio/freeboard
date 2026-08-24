## MODIFIED Requirements

### Requirement: Identity is derived from the credential and validated against the register

The endpoint SHALL treat the authenticated credential as authoritative for the
collector: the payload `collector_id` MUST equal the credential's collector, else
`422`. The run's vendor SHALL be taken from the collector's registered vendor
(the merged collector read model's `Vendor`); a registered collector whose vendor is null
CANNOT ingest and SHALL be rejected with `422` (missing vendor), with NO synthetic
`collector_id`-as-vendor fallback. The payload `requirement_id` MUST be one of the
collector control's mapped requirements (`ControlRow.MapsTo`), and the payload
`organisation_id` MUST resolve In-scope for that requirement through the Statement
of Applicability projection, else `422`. An unknown collector SHALL return `422`.

The in-scope gate SHALL be evaluated through the SAME Statement of Applicability
resolution the read surfaces use - the one that resolves over the unified asset tree by
walking `parent` edges - so ingest and the read surfaces cannot disagree about a node's
disposition for a requirement. Ingest SHALL NOT carry a second resolution rule of its
own.

The gate's ACCEPTANCE set SHALL remain ORGANISATION-only even though the resolution now
covers every asset in the `parent`-rooted forest: the payload `organisation_id` MUST
name a node that both resolves In-scope AND is a `Company` or `Department` asset. A
payload naming a `Machine` (or any other non-organisation asset) SHALL be rejected with
`422` even when that asset resolves In-scope for the requirement. A persisted run now
carries a machine in its own `asset_id` column, so a machine is a DIMENSION under the
run's organisation and never a substitute for it: the run's `organisation_id`, its
idempotency keys, and the evidence roll-ups built on them stay keyed on an organisation.
The `freeboard.evidence.v1` payload contract carries no machine field, so this endpoint
appends a run with a null `asset_id` and a null `cycle_id`. Letting a collector name the
machine it ran on is a separate change against that frozen contract.

Identity resolution SHALL read the one unified collector register. A collector of any
`type` - including `manual` and `training` - that is registered with a vendor MAY hold a
credential and ingest, exactly as before the collector merge; the endpoint SHALL NOT
reject a run on the basis of the collector's `type`.

#### Scenario: collector_id must match the credential

- **WHEN** the payload `collector_id` differs from the authenticated credential's
  collector
- **THEN** the response is `422 Unprocessable Entity`

#### Scenario: a null-vendor collector cannot ingest

- **WHEN** the authenticated collector is registered with no vendor
- **THEN** the response is `422 Unprocessable Entity` and nothing is appended

#### Scenario: requirement must belong to the collector's control

- **WHEN** the payload `requirement_id` is not a requirement of the collector's
  control
- **THEN** the response is `422 Unprocessable Entity`

#### Scenario: organisation must be in scope for the requirement

- **WHEN** the payload `organisation_id` is not In-scope for the requirement
- **THEN** the response is `422 Unprocessable Entity`

#### Scenario: A machine id is rejected even when it resolves in scope

- **WHEN** the payload `organisation_id` names a `Machine` asset whose `parent` chain
  resolves the requirement's standard `In`
- **THEN** the response is `422 Unprocessable Entity` and nothing is appended, because
  the acceptance set is the `Company` and `Department` nodes only

#### Scenario: An ingested run carries no machine and no cycle

- **WHEN** a valid `freeboard.evidence.v1` payload is accepted
- **THEN** the appended run records a null `asset_id` and a null `cycle_id`, because the
  wire contract defines neither field

#### Scenario: Ingest and the read surfaces resolve alike

- **WHEN** a department's disposition for a requirement is `Out` by inheritance from its
  company and a collector POSTs a run for that department
- **THEN** the response is `422 Unprocessable Entity`, matching the disposition the
  Statement of Applicability read surfaces report for that same node, because both are
  computed by the one asset-tree resolution

#### Scenario: Collector type does not gate ingest

- **WHEN** an authenticated collector of `type: manual` or `type: training` with a
  registered vendor POSTs a valid payload for an in-scope requirement
- **THEN** the run is appended, because the endpoint does not reject on collector `type`

### Requirement: The run verdict is derived, not posted

The endpoint SHALL derive the run-level result: the run is `Fail` when any check
has `severity` `hard` and `result` `fail`, and `Pass` otherwise. The collector
SHALL NOT post a run-level verdict. Soft-check failures SHALL NOT fail the run.

The endpoint SHALL NEVER append a run whose result is `Error`. The store's run result set
is `{Pass, Fail, Error}`, but `Error` records a collection attempt that failed to observe
anything, and this endpoint only ever receives an observation a collector already made and
delivered. A collector that could not collect SHALL NOT POST at all. `Error` belongs to
in-process collection, which appends the run itself.

#### Scenario: A hard-check failure fails the run

- **WHEN** a payload contains a check with `severity` `hard` and `result` `fail`
- **THEN** the appended run's result is `Fail`

#### Scenario: Only soft failures leave the run passing

- **WHEN** a payload's only failing checks are `severity` `soft`
- **THEN** the appended run's result is `Pass`

#### Scenario: The endpoint never records an error result

- **WHEN** any valid payload is accepted
- **THEN** the appended run's result is `Pass` or `Fail`, never `Error`

### Requirement: Idempotency and duplicate handling

Ingest SHALL be idempotent per collector run: the run's idempotency key SHALL be
the collector-namespaced reference so a re-delivery of the same
`(collector_id, run_id)` collides on the store's unique `(vendor, collector_ref)`
key. A duplicate SHALL return `200 OK` (accepted replay) echoing the request-
derived collector id, run id, and counts; nothing is mutated because evidence is
append-only. The endpoint SHALL NOT compare request bodies, so a differing body
under the same `run_id` is accepted as a replay rather than rejected.

The store carries a second idempotency key over the collection cycle, for runs that an
in-process collection appended. That key SHALL NOT change this endpoint's behaviour: an
ingested run leaves the cycle key's columns null, so the two keys cannot contend and
`(vendor, collector_ref)` stays the only key this contract depends on. The store admits
exactly one of the two identities per run, so an ingested run can never carry a cycle. The
documented wire contract and its JSON Schema are unchanged.

The store's `vendor` and `collector_ref` columns are nullable so that an in-process run need
not fabricate them, but an ingested run SHALL always carry both, so every ingested run stays
covered by the `(vendor, collector_ref)` key exactly as before. The endpoint already rejects
a collector with no registered vendor with a `422`, and it always composes `collector_ref`
itself.

#### Scenario: A re-delivered run is an accepted replay

- **WHEN** a collector re-POSTs the same `collector_id` and `run_id`
- **THEN** the response is `200 OK` and no second run is appended

#### Scenario: An ingested run always carries a vendor and a collector reference

- **WHEN** a valid payload is accepted
- **THEN** the appended run records a non-null `vendor` and a non-null `collector_ref`, so
  the replay key applies to it even though both columns are nullable in the store

#### Scenario: The cycle key does not affect ingest replay

- **WHEN** a collector re-POSTs the same `collector_id` and `run_id` after the cycle
  idempotency key exists on the store
- **THEN** the response is still `200 OK` from the `(vendor, collector_ref)` collision, and
  the request contract is unchanged
