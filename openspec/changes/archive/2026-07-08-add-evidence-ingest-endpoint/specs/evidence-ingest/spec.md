## ADDED Requirements

### Requirement: Authenticated Evidence ingest endpoint

The web app SHALL provide `POST /api/v1/freeboard/evidence` that accepts one collector run
as JSON and persists it as one immutable Evidence record with its nested checks. The
endpoint SHALL authenticate the caller with a per-collector machine bearer credential (see
the `collector-credentials` capability) and SHALL NOT accept a human session token. The
authenticated credential SHALL be authoritative for the collector identity: the endpoint
SHALL bind the run to the collector the credential is scoped to and SHALL snapshot that
collector's `title`, `control_id`, `vendor_id`, and `type` from the `evidence_collectors`
register onto the run at ingest.

The request body SHALL carry `schema_version` (exactly the v1 identifier `freeboard.evidence.v1`),
`collector_id`, `run_id`, `started_at`, `finished_at`, and a non-empty `checks` array, MAY
carry `collector_version`, and MAY carry a free-form `metadata` object. Each check SHALL
carry a `name`, a `severity` of `hard` or `soft`, a `status` of `pass`, `fail`, `unknown`, or
`not_applicable`, an optional `detail` string, and an optional `data` object. On a new run
the endpoint SHALL return `201 Created` with the Evidence id, `collector_id`, `run_id`,
`received_at`, and the summary counts (`hard_fail_count`, `soft_fail_count`, `total_count`).
The endpoint SHALL bind snake_case JSON member names exactly as the published contract
specifies.

#### Scenario: Valid run is accepted and persisted

- **WHEN** a caller presents a valid collector credential and POSTs a run whose
  `collector_id` matches the credential's collector, with `schema_version` set to the v1
  identifier and a non-empty `checks` array of valid severities and statuses
- **THEN** the response is `201 Created`, one immutable Evidence record and its checks are
  persisted with the collector identity snapshotted, and the body returns the Evidence id,
  `collector_id`, `run_id`, `received_at`, and the summary counts

#### Scenario: Missing or invalid credential is rejected

- **WHEN** a caller POSTs to the ingest endpoint with a missing, malformed, unknown-key, or
  hash-not-found collector credential
- **THEN** the response is `401` and no Evidence is persisted

#### Scenario: Revoked or expired credential is rejected

- **WHEN** a caller POSTs to the ingest endpoint with a recognised credential that has been
  revoked or has an elapsed expiry
- **THEN** the response is `403` and no Evidence is persisted

#### Scenario: Human session token is not accepted at ingest

- **WHEN** a caller presents a valid human session bearer token to the ingest endpoint
- **THEN** the response is `401` and no Evidence is persisted, because the endpoint accepts
  only collector credentials

### Requirement: Ingest payload validation

The endpoint SHALL handle the request body without automatic model binding: it SHALL read the
raw body bytes once, compute the exact-body hash over those bytes before deserialization (see
the idempotency requirement), then parse the JSON, then run the semantic validation pass. This
ordering is required so the boundary between `400` and `422` is deterministic: `400` SHALL mean
ONLY that the body is not well-formed JSON (a JSON syntax error, so the document cannot be
parsed at all). Every value-level problem in a well-formed JSON body SHALL be `422`, with no
exception for any field. A JSON type mismatch on ANY externally-supplied field is a value-level
problem and SHALL be `422`, not `400` - this includes a non-string `schema_version`,
`collector_id`, `run_id`, or `collector_version`; a non-string or otherwise non-scalar check
`name`, `severity`, or `status` (for example a numeric `severity`); a `checks` value that is not
an array; a `started_at`/`finished_at` that is absent, JSON null, not a string, unparseable as
ISO 8601, or not UTC; and a `metadata` or check `data` member that is present but is not a JSON
object.

To make this boundary hold for every field and prevent any future field from reintroducing the
gap, the endpoint SHALL parse the body into a JSON tree (a `JsonElement`/`JsonDocument`), which
fails ONLY on malformed JSON, and SHALL NOT strongly deserialize the body, or any single
externally-supplied field, into its typed .NET shape - a strongly-typed member throws during
deserialization on a JSON type mismatch, and that failure would surface as a framework `400`,
bypassing the `422` pass. Instead the semantic pass SHALL read every field from the parsed JSON
tree and validate its JSON value kind and value, and SHALL project the validated values into the
internal typed model only AFTER validation passes. Automatic binding is likewise not permitted,
because it would consume the body before hashing and would turn value-level shape errors into
framework `400`s.

The endpoint SHALL reject a malformed JSON body with `400 Bad Request` and an RFC 7807
problem body. The endpoint SHALL reject a semantically invalid payload with `422
Unprocessable Entity` and an RFC 7807 problem body, and SHALL NOT persist anything. A payload
is semantically invalid when: `schema_version` is not exactly `freeboard.evidence.v1`;
`collector_id` or `run_id` is missing, blank, or longer than 190 characters; the payload
`collector_id` does not match the authenticated credential's collector; the payload
`collector_id` names a collector not present in the `evidence_collectors` register;
`started_at` or `finished_at` is missing, JSON null, not a JSON string, not a UTC ISO 8601
timestamp, or `finished_at` precedes `started_at`; the `checks` array is absent or empty; any check `name` is blank,
longer than 190 characters, or duplicated within the run; any check has a `severity` outside
`{hard, soft}` or a `status` outside `{pass, fail, unknown, not_applicable}`; or any check
`data` or the top-level `metadata` is present but not a JSON object. Validation SHALL run
before any write so a rejected payload leaves the store unchanged.

The collector-id-vs-credential mismatch is a `422` (not a `403`): the credential is valid and
the request is authenticated, but the body is unprocessable against the authoritative
identity. This keeps `403` for a revoked or expired credential and `422` for a payload the
authenticated caller may not assert.

#### Scenario: Malformed JSON is rejected

- **WHEN** the request body is not well-formed JSON
- **THEN** the response is `400` with a problem body and no Evidence is persisted

#### Scenario: Wrong schema version is rejected

- **WHEN** `schema_version` is absent or not exactly `freeboard.evidence.v1`
- **THEN** the response is `422` with a problem body and no Evidence is persisted

#### Scenario: Collector id mismatch is rejected

- **WHEN** the payload `collector_id` differs from the collector the presented credential
  is scoped to
- **THEN** the response is `422` with a problem body and no Evidence is persisted

#### Scenario: Unknown collector is rejected

- **WHEN** the payload `collector_id` names a collector that is not in the
  `evidence_collectors` register
- **THEN** the response is `422` with a problem body and no Evidence is persisted

#### Scenario: Non-object metadata or check data is rejected as 422

- **WHEN** the top-level `metadata` or a check `data` member is present in a well-formed JSON
  body but is not a JSON object (for example a string, number, or array)
- **THEN** the response is `422` with a problem body and no Evidence is persisted, not a `400`

#### Scenario: Missing or malformed timestamp is rejected as 422

- **WHEN** a well-formed JSON body omits `started_at` or `finished_at`, sends one as JSON null,
  or sends one as a string that is not a UTC ISO 8601 timestamp
- **THEN** the response is `422` with a problem body and no Evidence is persisted, not a `400`,
  because a wrong or absent timestamp value in a well-formed JSON body is a semantic error

#### Scenario: JSON type mismatch on any field is rejected as 422

- **WHEN** a well-formed JSON body carries a wrong JSON value kind for any field - for example
  a numeric `collector_id` or `schema_version`, a numeric `severity` or `status`, or a `checks`
  value that is a string rather than an array
- **THEN** the response is `422` with a problem body and no Evidence is persisted, not a `400`,
  because a JSON type mismatch in a well-formed body is a value-level (semantic) error, not a
  JSON syntax error

#### Scenario: Bad check, timestamp, or duplicate name is rejected

- **WHEN** any check carries a `severity` other than `hard`/`soft` or a `status` other than
  `pass`/`fail`/`unknown`/`not_applicable`, the `checks` array is empty, two checks share a
  `name`, or `finished_at` precedes `started_at`
- **THEN** the response is `422` with a problem body and no Evidence is persisted

### Requirement: Ingest is idempotent on the run id

The endpoint SHALL be idempotent on `(collector_id, run_id)` keyed by an exact-body hash: it
SHALL compute the SHA-256 of the exact request body - over the raw received bytes, read once
before any deserialization, not a re-serialization of a parsed object - and store it with the
run. A re-POST of a
run whose `(collector_id, run_id)` already exists and whose body hash matches the stored hash
SHALL return `200 OK` with a replay indicator and the same body fields as the `201` response
(`evidence_id`, `collector_id`, `run_id`, `received_at`, `hard_fail_count`, `soft_fail_count`,
`total_count`), where `received_at` and the counts are the ORIGINAL stored values of the
existing run, not values derived from the replayed request; it SHALL NOT create a second record
or mutate the first. A re-POST of the same
`(collector_id, run_id)` with a different body hash SHALL return `409 Conflict` and SHALL NOT
mutate the stored run. Evidence is immutable and append-only. The `run_id` SHALL be supplied
by the caller; the endpoint SHALL NOT fabricate one.

#### Scenario: Re-POST of the same run and body is a replay

- **WHEN** a caller POSTs a run whose `(collector_id, run_id)` was already accepted with an
  identical body
- **THEN** the response is `200 OK` returning the existing Evidence id, a replay indicator, and
  the original stored `received_at` and summary counts of the existing run, and no second
  Evidence record is created and the first is not modified

#### Scenario: Re-POST of the same run with a different body is a conflict

- **WHEN** a caller POSTs a run whose `(collector_id, run_id)` was already accepted but with a
  different body
- **THEN** the response is `409 Conflict` and the stored run is not modified

#### Scenario: Distinct run ids create distinct records

- **WHEN** a collector POSTs two runs with the same `collector_id` but different `run_id`
  values
- **THEN** two distinct immutable Evidence records are persisted

### Requirement: Ingest bounds the payload size

The endpoint SHALL enforce an explicit maximum request body size of 1 MiB (1048576 bytes),
sized for evidence JSON rather than the framework default, and SHALL reject a body over the
limit with `413 Payload Too Large` before reading the whole body into memory.

#### Scenario: Oversized body is rejected

- **WHEN** a caller POSTs a body larger than 1 MiB
- **THEN** the response is `413` and no Evidence is persisted

### Requirement: Ingest reports a store failure distinctly

The endpoint SHALL return `503 Service Unavailable` with an RFC 7807 problem body when the
Evidence store is unavailable and the run could not be persisted, so a collector can retry
the same body safely.

#### Scenario: Store failure returns 503

- **WHEN** the Evidence write store fails to persist an otherwise valid, authenticated run
- **THEN** the response is `503` and the collector may safely retry the identical body

### Requirement: Ingest is exempt from GitOps read-only mode

The ingest endpoint SHALL be exempt from GitOps read-only mode so a read-only,
GitOps-managed instance still collects evidence. Even though ingest is a POST, the
read-only middleware SHALL NOT return `409` for it, because Evidence is runtime telemetry
and is not GitOps-managed config. The exemption SHALL be scoped to the ingest endpoint by a
dedicated marker distinct from the auth-endpoint marker (it SHALL NOT overload the
auth-endpoint marker), and SHALL NOT widen to other mutating routes. Credential issuance and
revocation SHALL NOT be exempt.

#### Scenario: Ingest works in read-only mode

- **WHEN** GitOps read-only mode is on and a collector POSTs a valid run with a valid
  credential
- **THEN** the run is accepted and persisted (not rejected with `409`), because the ingest
  endpoint carries the dedicated read-only exemption marker

#### Scenario: Credential issuance is still blocked in read-only mode

- **WHEN** GitOps read-only mode is on and an admin POSTs to the credential issuance endpoint
- **THEN** the request is rejected with `409`, because credential issuance is an admin config
  action and is not exempt

### Requirement: Ingest route is recognised as gated by the route-metadata guard

The ingest endpoint SHALL be gated by the per-collector authentication scheme and the named
ingest authorization policy rather than by a force-enforced permission, so it SHALL NOT carry
permission metadata. The build-time route-metadata guard that fails for any ungated mutating
API route SHALL recognise the ingest route as legitimately gated ONLY when it carries the
dedicated ingest marker AND is bound to the named ingest authorization policy; the guard SHALL
NOT exempt the ingest route by path alone. An ingest route that is missing the ingest marker or
the named-policy binding SHALL be flagged by the guard so an accidentally-ungated ingest route
cannot ship. A dedicated test SHALL assert that the ingest route carries the ingest marker and
that the named ingest policy binds the per-collector authentication scheme, so the route cannot
silently fall back to the default session scheme.

#### Scenario: Ingest route passes the route-metadata guard as gated

- **WHEN** the route-metadata guard evaluates the mutating `evidence` ingest route
- **THEN** it recognises the route as gated because the route carries the ingest marker and is
  bound to the named ingest authorization policy, not by a path-only exemption

#### Scenario: An ingest route missing its marker or policy binding fails the guard

- **WHEN** an ingest route is present without the ingest marker or without the named ingest
  policy binding
- **THEN** the route-metadata guard flags it as an ungated mutating API route so it cannot ship

### Requirement: Published ingest contract document and JSON Schema

The repository SHALL publish the ingest contract as both a human document at
`docs/evidence-ingest.md` (matching the style of `docs/gitops.md` and
`docs/authentication.md`) and a machine-readable JSON Schema at
`docs/schemas/evidence-ingest.v1.schema.json`. The human document SHALL specify the endpoint
route and method, the authentication model (per-collector bearer credential and how it is
issued, revoked, and optionally expired), the request schema (`schema_version`,
`collector_id`, `run_id`, `started_at`, `finished_at`, optional `collector_version`,
`checks[]` with `name`/`severity`/`status`/`detail`/`data`, optional `metadata`), the
severity semantics (`hard` vs `soft`, and that only `severity = hard` with `status = fail`
gates for V1), the status semantics (`pass`/`fail`/`unknown`/`not_applicable`), the
idempotency, replay, and conflict rules (`run_id` plus exact-body hash: `201` new, `200`
replay, `409` conflict), the success and error responses (`201`/`200`/`400`/`401`/`403`/`409`/
`413`/`422`/`503` and their RFC 7807 bodies), the body size limit (1 MiB), and a contract and
schema version so Ologist-side scripts can target a frozen schema. The document SHALL be plain
ASCII and SHALL pass markdownlint. A test SHALL validate the in-repo example payload(s)
against the JSON Schema so the docs, the schema, and the endpoint cannot silently drift.

#### Scenario: Contract documents the payload, auth, and idempotency

- **WHEN** a reader opens `docs/evidence-ingest.md`
- **THEN** it fully specifies the payload schema, severity and status semantics, the
  per-collector credential auth, the idempotency/replay/conflict rules, the error responses,
  the 1 MiB size limit, and a contract and schema version

#### Scenario: Example payload validates against the published JSON Schema

- **WHEN** the test suite validates the in-repo example payload against
  `docs/schemas/evidence-ingest.v1.schema.json`
- **THEN** the example conforms, proving the documented schema, the example, and the
  endpoint's accepted shape agree

### Requirement: Reference collector container packaging

The repository SHALL provide a reusable collector container wrapper under `collectors/`: a
base image and a curl-based `entrypoint.sh` that runs a collector script (which already emits
contract JSON on stdout), validates it, POSTs it to the ingest endpoint, retries safely with
the same body, and exits non-zero on ingest failure. The wrapper SHALL bake no .NET runtime
and no credentials into the image, SHALL run as a non-root user, and SHALL take its config
from environment variables: `FREEBOARD_BASE_URL`, `FREEBOARD_COLLECTOR_ID`,
`FREEBOARD_INGEST_TOKEN`, and optional `FREEBOARD_RUN_ID`. The repository SHALL include one
worked reference/mock example collector so a collector can be shown landing Evidence
end-to-end. The CE+ V1 vendor-specific collector scripts and their real image build inputs
are authored Ologist-side and are out of scope; the repository owns the reusable wrapper and
the one reference example, and documents the Ologist-side dependency.

#### Scenario: Reference container posts an example run and lands Evidence

- **WHEN** the reference container runs the worked example against a reachable instance with
  `FREEBOARD_BASE_URL`, `FREEBOARD_COLLECTOR_ID`, and `FREEBOARD_INGEST_TOKEN` set and a
  valid collector credential
- **THEN** the example run is POSTed to the ingest endpoint and an Evidence record with its
  checks is persisted for that collector

#### Scenario: Ingest failure exits non-zero and retries the same body

- **WHEN** the ingest POST fails transiently
- **THEN** the entrypoint retries with the identical body and, if ingest ultimately fails,
  exits non-zero so the orchestrator observes the failure
