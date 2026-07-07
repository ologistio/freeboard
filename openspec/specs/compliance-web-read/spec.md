# compliance-web-read Specification

## Purpose
TBD - created by archiving change add-gitops-mysql-persistence. Update Purpose after archive.
## Requirements
### Requirement: Web read endpoints serve the persisted compliance domain

The web app SHALL expose read-only HTTP endpoints that return the persisted
compliance domain from the store, not the YAML on disk. These endpoints live under
the single `/api/v1/freeboard/` API namespace and require an authenticated user. It
SHALL provide `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/requirements`, `GET /api/v1/freeboard/organisations`,
`GET /api/v1/freeboard/scopes`, and `GET /api/v1/freeboard/requirement-scopes`.
Standards SHALL include their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata (null when unset). Controls SHALL include their
`maps_to` `Requirement` ids, resolved from the `control_requirements` join.
Requirements SHALL include their owning `standard` id, `theme`, `statement`,
`guidance` (null when unset), and a `citation` object of `{ label, url }` composed
from the stored `citation_label` and `citation_url`. Organisations SHALL include
their `kind` and resolved `parent` id (null for a root). Scopes SHALL include their
`organisation` id, `standard` id, and `disposition`, resolved from the store.
Requirement-scopes SHALL include their `organisation` id, `requirement` id, and
`disposition`, resolved from the store. The web app SHALL read through the
`IComplianceStore` abstraction; its read-path dependency-injection registration
SHALL register `IComplianceStore` and SHALL NOT register the GitOps import or the
migration runner abstractions.

The org-scoped reads SHALL be narrowed to the caller's accessible organisation set
(as defined by the authorization enforcement capability): `organisations` filtered
by id, and `scopes` and `requirement-scopes` filtered by owning organisation. When a
returned organisation's `parent` is not in the caller's accessible set, its `parent`
id SHALL be nulled in the response, so the read does not disclose the existence of an
inaccessible ancestor; such a node reads as a root, consistent with how the selector
already treats it. The non-tenant catalog reads `standards`, `controls`, and
`requirements` are shared reference data with no confidentiality boundary and SHALL
NOT be narrowed; they remain authenticated-only.

Responses SHALL be deterministically ordered: resources SHALL be ordered by `id`
and each relation id array SHALL be ordered by id, using ordinal/binary order
consistent with the identifier identity semantics.

#### Scenario: Standards endpoint returns persisted standards with metadata

- **WHEN** a client requests `GET /api/v1/freeboard/standards`
- **THEN** the response lists the persisted standards with their `id`, `title`,
  and `version`, `authority`, `publisher`, and `source_url` metadata (null when
  unset)

#### Scenario: Controls endpoint includes cross-references

- **WHEN** a client requests `GET /api/v1/freeboard/controls`
- **THEN** the response lists the persisted controls with `id`, `title`, and the
  `maps_to` `Requirement` ids resolved from the store

#### Scenario: Requirements endpoint returns the requirement set

- **WHEN** a client requests `GET /api/v1/freeboard/requirements`
- **THEN** the response lists the persisted requirements with `id`, `title`,
  owning `standard` id, `theme`, `statement`, `guidance` (null when unset), and a
  `citation` object of `{ label, url }`, ordered by `id`

#### Scenario: Organisations endpoint returns the accessible tree

- **WHEN** a client requests `GET /api/v1/freeboard/organisations`
- **THEN** the response lists the persisted organisations in the caller's
  accessible set with `id`, `title`, `kind`, and resolved `parent` id (null for a
  root)

#### Scenario: Inaccessible parent id is not disclosed

- **WHEN** a caller reads `GET /api/v1/freeboard/organisations` and an accessible
  organisation's parent is not in the caller's accessible set
- **THEN** that organisation's `parent` id is null in the response rather than
  disclosing the inaccessible ancestor

#### Scenario: Scopes endpoint returns the accessible mapping

- **WHEN** a client requests `GET /api/v1/freeboard/scopes`
- **THEN** the response lists the scopes owned by organisations in the caller's
  accessible set with `id`, `title`, `organisation` id, `standard` id, and
  `disposition`

#### Scenario: Requirement-scopes endpoint returns the accessible mapping

- **WHEN** a client requests `GET /api/v1/freeboard/requirement-scopes`
- **THEN** the response lists the requirement-scopes owned by organisations in the
  caller's accessible set with `id`, `title`, `organisation` id, `requirement` id,
  and `disposition`, ordered by `id`

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
present with every per-kind key, each set to `null`. The per-kind key set includes
`vendors` and `vendorScopes` alongside the pre-existing kinds:

```json
{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null, "vendors": null, "vendorScopes": null } }
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

The two vendor read endpoints (`GET /api/v1/freeboard/vendors` and
`GET /api/v1/freeboard/vendor-scopes`) tolerate the unreachable store the same way
as the other resource reads: HTTP 503 with an RFC 7807 problem body, never an
unhandled exception.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null, "vendors": null, "vendorScopes": null }`
  rather than the request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`,
  `/api/v1/freeboard/requirements`, `/api/v1/freeboard/organisations`,
  `/api/v1/freeboard/scopes`, `/api/v1/freeboard/requirement-scopes`,
  `/api/v1/freeboard/vendors`, or `/api/v1/freeboard/vendor-scopes`
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
summary of how many standards, controls, requirements, organisations, scopes,
requirement-scopes, vendors, and vendor-scopes are currently persisted in the
store. This is the general compliance read surface; the persisted counts live here,
NOT on `GET /api/v1/freeboard/gitops/status` (which stays a GitOps concern
reporting read-only mode and repository URL). The summary SHALL be a `persisted`
object with per-kind counts:

```json
{ "persisted": { "standards": 3, "controls": 12, "requirements": 35, "organisations": 4, "scopes": 2, "requirementScopes": 3, "vendors": 5, "vendorScopes": 4 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, requirements, organisations, scopes, requirement-scopes,
  vendors, and vendor-scopes

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

### Requirement: Compliance reads require an authenticated user

Every compliance read endpoint SHALL require an authenticated user: the resource
reads `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/requirements`, `GET /api/v1/freeboard/organisations`,
`GET /api/v1/freeboard/scopes`, `GET /api/v1/freeboard/requirement-scopes`,
`GET /api/v1/freeboard/statement-of-applicability/{standardId}`, and
`GET /api/v1/freeboard/compliance/status`. Authentication (any logged-in user) is
sufficient; these reads SHALL NOT require the admin role. An anonymous request to
any of these endpoints SHALL return HTTP 401. Authentication is orthogonal to the
GitOps read-only gate: these GET endpoints SHALL still be served to an authenticated
user when the instance is in read-only mode.

#### Scenario: Anonymous read is rejected

- **WHEN** an anonymous client requests any compliance read endpoint (the resource
  reads including `requirements` and `requirement-scopes`, the
  statement-of-applicability endpoint, or `compliance/status`)
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

### Requirement: Vendor read endpoints serve the persisted vendor register

The web app SHALL expose read-only HTTP endpoints that return the persisted vendors
and vendor-scopes from the store, under the single `/api/v1/freeboard/` API
namespace, requiring an authenticated user (any logged-in user; no admin role). It
SHALL provide `GET /api/v1/freeboard/vendors` and
`GET /api/v1/freeboard/vendor-scopes`. Vendors SHALL include their `id` and
`title`. Vendor-scopes SHALL include their `id`, `title`, `vendor` id, the target
(`requirement` id or `control` id, whichever is set, with the other null),
`disposition`, and `justification` (null when unset). The `justification` SHALL
always be present in the payload for every `Out` vendor-scope, so an exception is
never silent. Both endpoints SHALL read through the `IComplianceStore` abstraction,
SHALL be GET-only and unaffected by GitOps read-only mode, and SHALL return the RFC
7807 / HTTP 503 unreachable-store response when the store is unavailable. Unlike the
per-org resource endpoints (`/organisations`, `/scopes`, `/requirement-scopes`),
which narrow rows to the caller's accessible organisation set via `IOrgAccess`, the
vendor endpoints intentionally do NOT filter: vendors and vendor-scopes are
org-independent reference data (the flat model of design D4, with no `organisation`
dimension), so any authenticated user - including one with zero org access - may
read every vendor and every vendor-scope, including the exception justifications.
This is deliberate and tied to Open Question Q1: if a later change adds an
organisation dimension to vendor-scopes, this access decision MUST be revisited.
Responses SHALL be deterministically ordered by `id`.

#### Scenario: Vendors endpoint returns the persisted vendors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendors`
- **THEN** the response lists the persisted vendors with their `id` and `title`,
  ordered by `id`

#### Scenario: Vendor-scopes endpoint returns exceptions with justifications

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendor-scopes`
- **THEN** the response lists each vendor-scope with its `vendor` id, target
  (`requirement` or `control` id), `disposition`, and `justification`, with the
  justification present for every `Out` row

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/vendors` or
  `GET /api/v1/freeboard/vendor-scopes`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests either
  vendor endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Zero-grant caller under strict enforcement still reads every vendor

- **WHEN** authorization runs in strict enforce mode and an authenticated caller
  holding no organisation grants requests `GET /api/v1/freeboard/vendors` or
  `GET /api/v1/freeboard/vendor-scopes`
- **THEN** both endpoints return every persisted vendor and vendor-scope, not narrowed
  to the empty accessible-organisation set that strict enforcement produces for a
  zero-grant caller, because the vendor endpoints intentionally skip the `IOrgAccess`
  narrowing that the per-org resource endpoints apply

