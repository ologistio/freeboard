## ADDED Requirements

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

## MODIFIED Requirements

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
