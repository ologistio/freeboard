# compliance-web-read Specification

## Purpose
TBD - created by archiving change add-gitops-mysql-persistence. Update Purpose after archive.
## Requirements
### Requirement: Web read endpoints serve the persisted compliance domain

The web app SHALL expose read-only HTTP endpoints that return the persisted
compliance domain from the store, not the YAML on disk. These endpoints live under
the single `/api/v1/freeboard/` API namespace. It SHALL provide
`GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`, and
`GET /api/v1/freeboard/scopes`. Controls SHALL include their `maps_to` Standard ids
and scopes SHALL include their `controls` Control ids, resolved from the store
relations. The web app SHALL read through the `IComplianceStore` abstraction; its
dependency-injection registration SHALL register only `IComplianceStore` and SHALL NOT
register the GitOps import or the migration runner abstractions.

Responses SHALL be deterministically ordered: resources SHALL be ordered by `id`
and each relation id array (`maps_to`, `controls`) SHALL be ordered by id, using
ordinal/binary order consistent with the identifier identity semantics.

#### Scenario: Standards endpoint returns persisted standards

- **WHEN** a client requests `GET /api/v1/freeboard/standards`
- **THEN** the response lists the persisted standards with their `id` and `title`

#### Scenario: Controls endpoint includes cross-references

- **WHEN** a client requests `GET /api/v1/freeboard/controls`
- **THEN** the response lists the persisted controls with `id`, `title`, and the
  `maps_to` Standard ids resolved from the store

#### Scenario: Scopes endpoint includes cross-references

- **WHEN** a client requests `GET /api/v1/freeboard/scopes`
- **THEN** the response lists the persisted scopes with `id`, `title`, and the
  `controls` Control ids resolved from the store

#### Scenario: Read responses are ordered by id

- **WHEN** a client requests any of the read endpoints
- **THEN** the resources are ordered by `id` and each `maps_to`/`controls` id
  array is ordered by id

### Requirement: Read path tolerates an unavailable store

The web app SHALL NOT auto-connect to MySQL at startup, so an unreachable store
SHALL NOT crash the app. When the store is unreachable at request time, a read
endpoint SHALL return a clear error response (an RFC 7807 problem body, HTTP 503)
rather than an unhandled exception, and the `GET /api/v1/freeboard/compliance/status`
endpoint's `persisted` summary SHALL degrade to all-null per-kind values rather
than failing the whole status response. The `persisted` object SHALL remain
present with every per-kind key, each set to `null`:

```json
{ "persisted": { "standards": null, "controls": null, "scopes": null } }
```

`null` (not omitted, not `{}`, not `0`) marks each count as unknown rather than
zero.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and a client requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "scopes": null }` rather than the
  request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and a client requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`, or
  `/api/v1/freeboard/scopes`
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception

### Requirement: Web tests inject a compliance store double

All web tests SHALL inject an `IComplianceStore` test double so `dotnet test`
stays green without a MySQL database. Web/double tests assert endpoint
serialization shape and ordering only; cross-reference (`maps_to`/`controls`)
resolution correctness from the SQL joins is asserted only by the MySQL
integration tests in the persistence capability, which skip when no MySQL is
reachable.

#### Scenario: Web tests run without MySQL

- **WHEN** the web test suite runs with no MySQL available
- **THEN** every web test injects an `IComplianceStore` double and the suite is
  green

### Requirement: Read endpoints are GET-only and unaffected by read-only mode

The compliance read endpoints SHALL serve only `GET` requests. Because they are
read-only, the existing GitOps read-only middleware SHALL NOT block them when
read-only mode is on.

#### Scenario: Read endpoints work in read-only mode

- **WHEN** read-only mode is on and a client issues a `GET` to a compliance read
  endpoint
- **THEN** the request is served normally and is not rejected with the
  read-only-mode 409 response

### Requirement: Compliance status endpoint reports persisted counts

The web app SHALL provide `GET /api/v1/freeboard/compliance/status` returning a
summary of how many standards, controls, and scopes are currently persisted in the
store. This is the general compliance read surface; the persisted counts live here,
NOT on `GET /api/v1/freeboard/gitops/status` (which stays a GitOps concern reporting
read-only mode and repository URL). The summary SHALL be a `persisted` object with
per-kind counts:

```json
{ "persisted": { "standards": 3, "controls": 12, "scopes": 2 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, and scopes

### Requirement: GitOps status endpoint is unchanged and store-independent

`GET /api/v1/freeboard/gitops/status` SHALL continue to return ONLY its existing
fields - the `gitOps` boolean and `repositoryUrl` (present only when a repository URL
is set) - and SHALL NOT include a `persisted` summary or any persisted-count field.
The gitops status endpoint and its handler SHALL NOT depend on `IComplianceStore`: it
SHALL serve its response without requiring the store to be reachable or even
registered. (Its path moves under the `/api/v1/freeboard/` namespace.)

#### Scenario: GitOps status shape is unchanged

- **WHEN** a client requests `GET /api/v1/freeboard/gitops/status`
- **THEN** the response contains only `gitOps` (and `repositoryUrl` when set) and
  does NOT include a `persisted` summary

#### Scenario: GitOps status does not depend on the compliance store

- **WHEN** a client requests `GET /api/v1/freeboard/gitops/status` with no
  `IComplianceStore` available or with the store unreachable
- **THEN** the endpoint still returns its normal `gitOps`/`repositoryUrl` response
  rather than failing

