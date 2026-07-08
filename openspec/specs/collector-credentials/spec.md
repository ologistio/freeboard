# collector-credentials Specification

## Purpose
TBD - created by archiving change rework-evidence-ingest-shared-model. Update Purpose after archive.
## Requirements
### Requirement: Per-collector machine credential

The system SHALL support a per-collector machine credential: a bearer token
scoped to exactly one evidence-collector, carrying no human identity, revocable,
with an optional expiry. Credentials SHALL be stored in a `collector_credentials`
table that keeps only the keyed HMAC of the token and its key version; the raw
token SHALL never be persisted. The credential row SHALL foreign-key
`evidence_collectors` ON DELETE CASCADE, so revoking the collector removes its
credentials. The token SHALL share the existing `v<keyId>.<secret>` wire format
and reuse the existing token hasher.

#### Scenario: Only the keyed hash is stored

- **WHEN** a credential is issued
- **THEN** the raw token is returned once to the caller
- **AND** the persisted row holds only the token's keyed HMAC and key version

#### Scenario: Deleting a collector cascades its credentials

- **WHEN** an evidence-collector is deleted
- **THEN** its `collector_credentials` rows are removed

### Requirement: Collector bearer authentication scheme and ingest policy

The system SHALL add a collector bearer authentication scheme, separate from the
human session scheme, that authenticates only against `collector_credentials`. An
absent, malformed, unknown, or key-version-mismatched token SHALL fail
authentication (`401`). A recognised credential SHALL authenticate and carry the
collector-id claim, and SHALL carry an active claim only when it is neither
revoked nor expired. The named ingest authorization policy SHALL bind this scheme
and require both the collector-id claim and the active claim, so a revoked or
expired credential authenticates but is forbidden (`403`). Presenting a collector
token at any non-ingest endpoint SHALL fail with `401`.

#### Scenario: A revoked credential is forbidden, not unauthenticated

- **WHEN** a request presents a recognised but revoked collector token at ingest
- **THEN** the response is `403 Forbidden`

#### Scenario: An unknown token fails authentication

- **WHEN** a request presents a token with no matching credential
- **THEN** the response is `401 Unauthorized`

#### Scenario: A collector token is rejected outside ingest

- **WHEN** a collector token is presented at a human-session endpoint
- **THEN** the response is `401 Unauthorized`

### Requirement: Credential issuance and revocation are system-admin config actions

The system SHALL expose `POST /api/v1/freeboard/evidence-collectors/{id}/credentials`
to issue a credential and
`DELETE /api/v1/freeboard/evidence-collectors/{id}/credentials/{credId}` to revoke
one. Both SHALL require the system-admin permission with force-enforce and SHALL
NOT carry the ingest marker, so in GitOps read-only mode they return `409`.
Issuance SHALL return `201` with the raw token exactly once and `422` for an
unknown collector; issuance MAY accept an optional ISO 8601 expiry. Revocation
SHALL return `204` when a live credential was revoked and `404` when it does not
exist under that collector or was already revoked. The CLI SHALL provide
`freeboard collector credential issue` (printing the raw token once) and
`freeboard collector credential revoke`, both through the HTTP API only.

#### Scenario: Issuing returns the token once

- **WHEN** a system admin issues a credential for a known collector
- **THEN** the response is `201 Created` with the raw token shown once

#### Scenario: Issuing for an unknown collector is rejected

- **WHEN** a system admin issues a credential for a collector that does not exist
- **THEN** the response is `422 Unprocessable Entity`

#### Scenario: Credential admin is blocked in read-only mode

- **WHEN** the instance is in GitOps read-only mode and a credential issue or
  revoke is requested
- **THEN** the response is `409 Conflict`

#### Scenario: Revoking a missing credential is a 404

- **WHEN** a revoke targets a credential that does not exist under the collector
  or was already revoked
- **THEN** the response is `404 Not Found`

