## ADDED Requirements

### Requirement: Authenticated evidence ingest endpoint

The system SHALL expose `POST /api/v1/freeboard/evidence` that accepts a
versioned JSON evidence payload from a collector and appends one immutable
evidence run through the shared evidence write store (`IEvidenceWriteStore`). The
route SHALL be authorized only by the named ingest policy bound to the collector
bearer scheme, NOT by the default human session scheme. A well-formed accepted
payload SHALL append exactly one evidence run with its checks in a single
transaction and return `201 Created`. The system SHALL NOT introduce a second
evidence persistence store; the ingest endpoint reuses the existing evidence
model.

#### Scenario: A valid payload appends one evidence run

- **WHEN** a request with an active collector credential POSTs a valid
  `freeboard.evidence.v1` payload
- **THEN** one evidence run and its checks are appended via `IEvidenceWriteStore`
- **AND** the response is `201 Created` with the collector id, run id, and the
  derived hard-fail, soft-fail, and total counts

#### Scenario: A session token is rejected at ingest

- **WHEN** a request presents a human session bearer token at the ingest route
- **THEN** the response is `401 Unauthorized` and nothing is appended

### Requirement: Evidence payload contract (freeboard.evidence.v1)

The request body SHALL be a JSON object carrying `schema_version` (exactly
`freeboard.evidence.v1`), `collector_id`, `run_id`, `organisation_id`,
`requirement_id`, `collected_at` (UTC ISO 8601 with a `Z` or `+00:00`
designator), and a non-empty `checks` array. Each check SHALL carry `name`
(non-blank, unique within the run, at most 190 characters), `severity` (`hard` or
`soft`), `result` (`pass` or `fail`), and an optional `detail`. The published
JSON Schema (`docs/schemas/evidence-ingest.v1.schema.json`) and
`docs/evidence-ingest.md` SHALL be the frozen reference. A well-formed body that
violates any value or type rule SHALL return `422 Unprocessable Entity`; only a
JSON syntax error SHALL return `400 Bad Request`. The exact submitted body SHALL
be stored as the run's raw payload so provenance fields without a typed column
are retained.

#### Scenario: A wrong-typed field is a 422, not a 400

- **WHEN** a well-formed JSON body has a check `severity` that is not `hard` or
  `soft`
- **THEN** the response is `422 Unprocessable Entity` and nothing is appended

#### Scenario: Malformed JSON is a 400

- **WHEN** the request body is not well-formed JSON
- **THEN** the response is `400 Bad Request` and nothing is appended

#### Scenario: The full body is retained as raw payload

- **WHEN** a valid payload carrying extra provenance fields is accepted
- **THEN** the appended run's raw payload holds the exact submitted JSON body

### Requirement: Identity is derived from the credential and validated against the register

The endpoint SHALL treat the authenticated credential as authoritative for the
collector: the payload `collector_id` MUST equal the credential's collector, else
`422`. The run's vendor SHALL be taken from the collector's registered vendor
(`EvidenceCollectorRow.Vendor`); a registered collector whose vendor is null
CANNOT ingest and SHALL be rejected with `422` (missing vendor), with NO synthetic
`collector_id`-as-vendor fallback. The payload `requirement_id` MUST be one of the
collector control's mapped requirements (`ControlRow.MapsTo`), and the payload
`organisation_id` MUST resolve In-scope for that requirement through the Statement
of Applicability projection, else `422`. An unknown collector SHALL return `422`.

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

### Requirement: The run verdict is derived, not posted

The endpoint SHALL derive the run-level result: the run is `Fail` when any check
has `severity` `hard` and `result` `fail`, and `Pass` otherwise. The collector
SHALL NOT post a run-level verdict. Soft-check failures SHALL NOT fail the run.

#### Scenario: A hard-check failure fails the run

- **WHEN** a payload contains a check with `severity` `hard` and `result` `fail`
- **THEN** the appended run's result is `Fail`

#### Scenario: Only soft failures leave the run passing

- **WHEN** a payload's only failing checks are `severity` `soft`
- **THEN** the appended run's result is `Pass`

### Requirement: Idempotency and duplicate handling

Ingest SHALL be idempotent per collector run: the run's idempotency key SHALL be
the collector-namespaced reference so a re-delivery of the same
`(collector_id, run_id)` collides on the store's unique `(vendor, collector_ref)`
key. A duplicate SHALL return `200 OK` (accepted replay) echoing the request-
derived collector id, run id, and counts; nothing is mutated because evidence is
append-only. The endpoint SHALL NOT compare request bodies, so a differing body
under the same `run_id` is accepted as a replay rather than rejected.

#### Scenario: A re-delivered run is an accepted replay

- **WHEN** a collector re-POSTs the same `collector_id` and `run_id`
- **THEN** the response is `200 OK` and no second run is appended

### Requirement: Request size limit

The endpoint SHALL cap the request body at 1 MiB and return `413 Payload Too
Large` when the body exceeds it, without appending anything.

#### Scenario: An oversize body is rejected

- **WHEN** the request body exceeds 1 MiB
- **THEN** the response is `413 Payload Too Large` and nothing is appended

### Requirement: Ingest works in GitOps read-only mode

The ingest route SHALL carry the ingest endpoint metadata marker so the GitOps
read-only middleware exempts it from the read-only `409`: evidence is runtime
data, not GitOps-authored config. The route-authz guard test SHALL recognise the
marked ingest route (bound to the ingest policy) as legitimately gated by the
collector scheme rather than by a permission filter.

#### Scenario: A collector lands evidence in read-only mode

- **WHEN** the instance is in GitOps read-only mode and a collector POSTs valid
  evidence
- **THEN** the evidence is appended and the response is `201 Created`
