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

#### Scenario: Collector type does not gate ingest

- **WHEN** an authenticated collector of `type: manual` or `type: training` with a
  registered vendor POSTs a valid payload for an in-scope requirement
- **THEN** the run is appended, because the endpoint does not reject on collector `type`

### Requirement: Ingest records the collecting collector identity and cadence

The ingest endpoint SHALL record, on the appended evidence run, the resolved collector's
id (`collector_id`) and its `frequency` cadence token, so downstream staleness evaluation
can group evidence by collector and judge overdue-ness from the run itself without reading
the mutable collector configuration. Both SHALL be taken from the authenticated
collector: the `collector_id` from the credential-validated payload collector id, and the
cadence from the collector's registration in the unified collector register, which the
endpoint already resolves to validate the vendor and requirement mapping. The collector
SHALL NOT post either value as a staleness input, so the `freeboard.evidence.v1` payload
contract is unchanged. When the resolved collector's `frequency` is blank, the run SHALL
record a null cadence and SHALL NOT be rejected for that reason; such a run is simply never
evaluated as stale.

#### Scenario: The appended run carries the collector id and cadence

- **WHEN** a collector with a `daily` cadence POSTs a valid `freeboard.evidence.v1`
  payload
- **THEN** the appended run records the collector's id as its `collector_id` and `daily`
  as its cadence, both taken from the collector's registration rather than from the
  payload body

#### Scenario: The wire payload does not carry a cadence

- **WHEN** a payload includes a `frequency` field
- **THEN** the recorded cadence is still the collector's registered cadence, because the
  cadence is server-derived and the payload contract does not define a client-supplied
  cadence

#### Scenario: A blank registered cadence records null

- **WHEN** the resolved collector has a blank `frequency`
- **THEN** the run is appended with a null cadence and is not rejected for the missing
  cadence
