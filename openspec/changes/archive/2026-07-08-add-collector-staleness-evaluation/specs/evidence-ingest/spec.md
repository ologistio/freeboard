## ADDED Requirements

### Requirement: Ingest records the collecting collector identity and cadence

The ingest endpoint SHALL record, on the appended evidence run, the resolved collector's
id (`collector_id`) and its `frequency` cadence token, so downstream staleness evaluation
can group evidence by collector and judge overdue-ness from the run itself without reading
the mutable collector configuration. Both SHALL be taken from the authenticated
collector: the `collector_id` from the credential-validated payload collector id, and the
cadence from the collector's registration (`EvidenceCollectorRow.Frequency`), which the
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
