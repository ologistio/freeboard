## MODIFIED Requirements

### Requirement: Vendor read endpoints serve the persisted vendor register

The web app SHALL expose read-only HTTP endpoints that return the persisted vendors
and vendor-scopes THE CALLER MAY READ from the store, under the single
`/api/v1/freeboard/` API namespace, requiring an authenticated user (any logged-in
user; no admin role). It SHALL provide `GET /api/v1/freeboard/vendors` and
`GET /api/v1/freeboard/vendor-scopes`. Vendors are `Asset` rows of `type: Vendor`
and SHALL include their `id` and `title`. Vendor-scopes SHALL include their `id`,
`title`, `vendor` id, the target (`requirement` id or `control` id, whichever is set,
with the other null), `disposition`, and `justification` (null when unset). The
`justification` SHALL always be present in the payload for every readable `Out`
vendor-scope, so an exception is never silent. Both endpoints SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only
mode, and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the
store is unavailable. Responses SHALL be deterministically ordered by `id`.

Both endpoints SHALL narrow their rows to the caller's accessible organisation set
through the vendor `owner` edge, replacing the prior global-readability behavior. A
vendor is readable only when its `owner` (a `Company`/`Department` asset) is in the
caller's accessible-organisation set (as defined by the authorization enforcement
capability); a vendor-scope is readable only when its vendor is readable. The prior
behavior - every authenticated user, including a zero-grant caller, reading every
vendor and every vendor-scope regardless of grants - SHALL NOT apply. Read-access is
fail-closed: a vendor with a missing or dangling `owner`, or an `owner` outside the
caller's accessible set, SHALL have BOTH its vendor row AND its vendor-scopes (target,
disposition, and `Out` justification) hidden from that caller, so neither the vendor
id nor its exception rationale leaks through the vendor-scope list even though the
vendor row is suppressed.

#### Scenario: Vendors endpoint returns the readable vendors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendors` and some
  vendors have an `owner` in the caller's accessible-organisation set
- **THEN** the response lists those vendors with their `id` and `title`, ordered by
  `id`, and omits any vendor whose `owner` is missing, dangling, or outside the
  accessible set

#### Scenario: Vendor-scopes endpoint returns readable exceptions with justifications

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendor-scopes`
- **THEN** the response lists each vendor-scope whose vendor is readable (its `owner`
  in the caller's accessible set) with its `vendor` id, target (`requirement` or
  `control` id), `disposition`, and `justification`, with the justification present
  for every readable `Out` row, and omits every vendor-scope whose vendor is hidden

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/vendors` or
  `GET /api/v1/freeboard/vendor-scopes`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests either
  vendor endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Owner-excluded caller sees neither the vendor nor its vendor-scopes

- **WHEN** an authenticated caller with no grant reaching a vendor's `owner` (or the
  vendor has a missing or dangling `owner`) requests `GET /api/v1/freeboard/vendors`
  or `GET /api/v1/freeboard/vendor-scopes`
- **THEN** neither the vendor row nor any of that vendor's vendor-scopes or `Out`
  justifications appear in either response, because vendor readability follows the
  `owner` edge and is not global, so the hidden vendor's id and exception rationale do
  not leak through the vendor-scope list
