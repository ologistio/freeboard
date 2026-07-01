## ADDED Requirements

### Requirement: Compliance reads require an authenticated user

Every compliance read endpoint SHALL require an authenticated user: the resource
reads `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/organisations`, `GET /api/v1/freeboard/scopes`,
`GET /api/v1/freeboard/statement-of-applicability/{standardId}`, and
`GET /api/v1/freeboard/compliance/status`. Authentication (any logged-in user) is
sufficient; these reads SHALL NOT require the admin role. An anonymous request to
any of these endpoints SHALL return HTTP 401. Authentication is orthogonal to the
GitOps read-only gate: these GET endpoints SHALL still be served to an authenticated
user when the instance is in read-only mode.

#### Scenario: Anonymous read is rejected

- **WHEN** an anonymous client requests any compliance read endpoint (the resource
  reads, the statement-of-applicability endpoint, or `compliance/status`)
- **THEN** the endpoint returns HTTP 401

#### Scenario: Any authenticated user may read without the admin role

- **WHEN** an authenticated non-admin user requests a compliance read endpoint with
  a reachable store
- **THEN** the endpoint returns HTTP 200 with the read data, without requiring the
  admin role

#### Scenario: Reads are served to an authenticated user in read-only mode

- **WHEN** the instance is in GitOps read-only mode and an authenticated user
  requests a compliance read endpoint
- **THEN** the endpoint returns HTTP 200, because read-only mode blocks only
  mutating methods

## MODIFIED Requirements

### Requirement: Web read endpoints serve the persisted compliance domain

The web app SHALL expose read-only HTTP endpoints that return the persisted
compliance domain from the store, not the YAML on disk. These endpoints live under
the single `/api/v1/freeboard/` API namespace and require an authenticated user. It
SHALL provide `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/organisations`, and `GET /api/v1/freeboard/scopes`. Controls
SHALL include their `maps_to` Standard ids. Organisations SHALL include their
`kind` and resolved `parent` id (null for a root). Scopes SHALL include their
`organisation` id, `standard` id, and `disposition`, resolved from the store. The
web app SHALL read through the `IComplianceStore` abstraction; its read-path
dependency-injection registration SHALL register `IComplianceStore` and SHALL NOT
register the GitOps import or the migration runner abstractions.

Responses SHALL be deterministically ordered: resources SHALL be ordered by `id`
and each relation id array SHALL be ordered by id, using ordinal/binary order
consistent with the identifier identity semantics.

#### Scenario: Standards endpoint returns persisted standards

- **WHEN** a client requests `GET /api/v1/freeboard/standards`
- **THEN** the response lists the persisted standards with their `id` and `title`

#### Scenario: Controls endpoint includes cross-references

- **WHEN** a client requests `GET /api/v1/freeboard/controls`
- **THEN** the response lists the persisted controls with `id`, `title`, and the
  `maps_to` Standard ids resolved from the store

#### Scenario: Organisations endpoint returns the tree

- **WHEN** a client requests `GET /api/v1/freeboard/organisations`
- **THEN** the response lists the persisted organisations with `id`, `title`,
  `kind`, and resolved `parent` id (null for a root)

#### Scenario: Scopes endpoint returns the mapping

- **WHEN** a client requests `GET /api/v1/freeboard/scopes`
- **THEN** the response lists the persisted scopes with `id`, `title`,
  `organisation` id, `standard` id, and `disposition`

#### Scenario: Read responses are ordered by id

- **WHEN** a client requests any of the read endpoints
- **THEN** the resources are ordered by `id` and each relation id array is ordered
  by id

### Requirement: Read path tolerates an unavailable store

The web app SHALL NOT auto-connect to MySQL at startup, so an unreachable store
SHALL NOT crash the app. When the store is unreachable at request time, a read
endpoint SHALL return a clear error response (an RFC 7807 problem body, HTTP 503)
rather than an unhandled exception, and the `GET /api/v1/freeboard/compliance/status`
endpoint's `persisted` summary SHALL degrade to all-null per-kind values rather
than failing the whole status response. The `persisted` object SHALL remain
present with every per-kind key, each set to `null`:

```json
{ "persisted": { "standards": null, "controls": null, "organisations": null, "scopes": null } }
```

`null` (not omitted, not `{}`, not `0`) marks each count as unknown rather than
zero.

Authentication precedes every compliance read and shares the same backing store as
the compliance store. So these degradation responses (HTTP 503 for the resource
reads, HTTP 200 with an all-null persisted summary for `compliance/status`) describe
the case where the request is authenticated and only the compliance store is
unavailable to it. A full database outage that also fails authentication surfaces
first as an authentication failure (HTTP 401) - the request never reaches the
compliance handler - not as these compliance degradation responses.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "organisations": null, "scopes": null }`
  rather than the request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`,
  `/api/v1/freeboard/organisations`, or `/api/v1/freeboard/scopes`
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception

### Requirement: Compliance status endpoint reports persisted counts

The web app SHALL provide `GET /api/v1/freeboard/compliance/status` returning a
summary of how many standards, controls, organisations, and scopes are currently
persisted in the store. This is the general compliance read surface; the persisted
counts live here, NOT on `GET /api/v1/freeboard/gitops/status` (which stays a
GitOps concern reporting read-only mode and repository URL). The summary SHALL be a
`persisted` object with per-kind counts:

```json
{ "persisted": { "standards": 3, "controls": 12, "organisations": 4, "scopes": 2 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, organisations, and scopes
