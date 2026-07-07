## ADDED Requirements

### Requirement: Evidence-collector read endpoint serves the persisted collectors

The web app SHALL expose a read-only HTTP endpoint that returns the persisted
evidence-collectors from the store, under the single `/api/v1/freeboard/` API
namespace, requiring an authenticated user (any logged-in user; no admin role). It
SHALL provide `GET /api/v1/freeboard/evidence-collectors`. Each collector SHALL
include its `id`, `title`, `control` id, `vendor` id (null when unset), `type`,
`frequency`, `threshold` (null when unset), and `config` (the type-specific settings
map, an empty object when unset). The endpoint SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only
mode, and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the
store is unavailable. Unlike the per-org resource endpoints (`/organisations`,
`/scopes`, `/requirement-scopes`), which narrow rows to the caller's accessible
organisation set via `IOrgAccess`, this endpoint intentionally does NOT filter:
evidence-collectors are org-independent reference data (they carry no `organisation`
dimension), so any authenticated user - including one with zero org access - may read
every collector. Responses SHALL be deterministically ordered by `id`.

#### Scenario: Evidence-collectors endpoint returns the persisted collectors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/evidence-collectors`
- **THEN** the response lists each collector with its `id`, `title`, `control`,
  `vendor` (null when unset), `type`, `frequency`, `threshold` (null when unset), and
  `config`, ordered by `id`

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/evidence-collectors`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests the
  evidence-collectors endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Zero-grant caller under strict enforcement still reads every collector

- **WHEN** authorization runs in strict enforce mode and an authenticated caller
  holding no organisation grants requests `GET /api/v1/freeboard/evidence-collectors`
- **THEN** the endpoint returns every persisted collector, not narrowed to the empty
  accessible-organisation set that strict enforcement produces for a zero-grant
  caller, because the collector endpoint intentionally skips the `IOrgAccess`
  narrowing that the per-org resource endpoints apply

## MODIFIED Requirements

### Requirement: Web read endpoints serve the persisted compliance domain

The web app SHALL expose read-only HTTP endpoints that return the persisted
compliance domain from the store, not the YAML on disk. These endpoints live under
the single `/api/v1/freeboard/` API namespace and require an authenticated user. It
SHALL provide `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/requirements`, `GET /api/v1/freeboard/organisations`,
`GET /api/v1/freeboard/scopes`, and `GET /api/v1/freeboard/requirement-scopes`.
Standards SHALL include their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata (null when unset). Controls SHALL include their
`maps_to` `Requirement` ids, resolved from the `control_requirements` join, and their
`evaluation` rule (null when unset). Requirements SHALL include their owning
`standard` id, `theme`, `statement`, `guidance` (null when unset), and a `citation`
object of `{ label, url }` composed from the stored `citation_label` and
`citation_url`. Organisations SHALL include their `kind` and resolved `parent` id
(null for a root). Scopes SHALL include their `organisation` id, `standard` id, and
`disposition`, resolved from the store. Requirement-scopes SHALL include their
`organisation` id, `requirement` id, and `disposition`, resolved from the store. The
web app SHALL read through the `IComplianceStore` abstraction; its read-path
dependency-injection registration SHALL register `IComplianceStore` and SHALL NOT
register the GitOps import or the migration runner abstractions.

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
- **THEN** the response lists the persisted controls with `id`, `title`, the
  `maps_to` `Requirement` ids resolved from the store, and the `evaluation` rule
  (null when unset)

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

### Requirement: Compliance status endpoint reports persisted counts

The web app SHALL provide `GET /api/v1/freeboard/compliance/status` returning a
summary of how many standards, controls, requirements, organisations, scopes,
requirement-scopes, vendors, vendor-scopes, and evidence-collectors are currently
persisted in the store. This is the general compliance read surface; the persisted
counts live here, NOT on `GET /api/v1/freeboard/gitops/status` (which stays a GitOps
concern reporting read-only mode and repository URL). The summary SHALL be a
`persisted` object with per-kind counts:

```json
{ "persisted": { "standards": 3, "controls": 12, "requirements": 35, "organisations": 4, "scopes": 2, "requirementScopes": 3, "vendors": 5, "vendorScopes": 4, "evidenceCollectors": 8 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, requirements, organisations, scopes, requirement-scopes,
  vendors, vendor-scopes, and evidence-collectors

### Requirement: Read path tolerates an unavailable store

The web app SHALL NOT auto-connect to MySQL at startup, so an unreachable store
SHALL NOT crash the app. When the store is unreachable at request time, a read
endpoint SHALL return a clear error response (an RFC 7807 problem body, HTTP 503)
rather than an unhandled exception, and the `GET /api/v1/freeboard/compliance/status`
endpoint's `persisted` summary SHALL degrade to all-null per-kind values rather
than failing the whole status response. The `persisted` object SHALL remain
present with every per-kind key, each set to `null`. The per-kind key set includes
`vendors`, `vendorScopes`, and `evidenceCollectors` alongside the pre-existing kinds:

```json
{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null, "vendors": null, "vendorScopes": null, "evidenceCollectors": null } }
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

The vendor and evidence-collector read endpoints
(`GET /api/v1/freeboard/vendors`, `GET /api/v1/freeboard/vendor-scopes`, and
`GET /api/v1/freeboard/evidence-collectors`) tolerate the unreachable store the same
way as the other resource reads: HTTP 503 with an RFC 7807 problem body, never an
unhandled exception.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null, "vendors": null, "vendorScopes": null, "evidenceCollectors": null }`
  rather than the request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`,
  `/api/v1/freeboard/requirements`, `/api/v1/freeboard/organisations`,
  `/api/v1/freeboard/scopes`, `/api/v1/freeboard/requirement-scopes`,
  `/api/v1/freeboard/vendors`, `/api/v1/freeboard/vendor-scopes`, or
  `/api/v1/freeboard/evidence-collectors`
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception
