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
`422` even when that asset resolves In-scope for the requirement, because the persisted
run's `organisation_id`, its collector-scoped idempotency key, and the evidence
roll-ups built on them are all keyed on an organisation. Admitting a machine as an
evidence subject is a separate change that moves those with it.

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
