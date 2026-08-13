## MODIFIED Requirements

### Requirement: Vendor read endpoints serve the persisted vendor register

The web app SHALL expose a read-only HTTP endpoint that returns the persisted vendors THE
CALLER MAY READ from the store, under the single `/api/v1/freeboard/` API namespace,
requiring an authenticated user (any logged-in user; no admin role). It SHALL provide
`GET /api/v1/freeboard/vendors`. Vendors are `Asset` rows of `type: Vendor`, served from the
one unified asset read rather than a separate vendor read, and SHALL include their `id`,
`title`, and the vendor-only facets the register renders: `tier`, `data_classes`, and
`assurances` (each carrying its `standard`, its `expires`, and its derived `status`).
The row SHALL carry no other asset column: the discovered-only machine fields and the
`owner` edge are not part of this projection. The endpoint SHALL derive an assurance's
status rather than returning the expiry alone, so the warning window that decides it
lives in one process and the web page and the CLI cannot disagree about the state of the
same certification. A vendor's per-requirement and per-control exceptions are
served by the unified `GET /api/v1/freeboard/scopes` endpoint as scopes whose `subject` is
that vendor (there is no separate `/vendor-scopes` endpoint); those scopes carry the
target, `disposition`, and `justification` (always present for a readable `Out` scope, so
an exception is never silent). The `/vendors` endpoint SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only mode,
and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the store is
unavailable. Responses SHALL be deterministically ordered by `id`.

The `/vendors` endpoint SHALL narrow its rows by the SAME accessible-asset-set membership test
every other narrowed read uses, rather than by a vendor-specific owner check of its own: a
vendor is returned when its id is in the caller's accessible asset set, which the
authorization enforcement capability admits exactly when the vendor's `owner` is in the
caller's organisation union. The unified `/scopes` endpoint narrows vendor-subject scopes by
the same test on the scope's `subject`. Read-access is fail-closed: a vendor with a
missing or dangling `owner`, or an `owner` outside the caller's accessible set, SHALL have
BOTH its vendor row (on `/vendors`) AND its vendor-subject scopes (on `/scopes`) hidden
from that caller, so neither the vendor id nor its exception rationale leaks even though
the vendor row is suppressed. Every facet on the row is hidden with it, because the row
itself is absent rather than redacted.

#### Scenario: Vendors endpoint returns the readable vendors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendors` and some
  vendors have an `owner` that resolves into the caller's organisation union
- **THEN** the response lists those vendors with their `id`, `title`, `tier`,
  `data_classes`, and `assurances`, ordered by `id`, and omits any vendor whose `owner`
  is missing, dangling, or outside the accessible set

#### Scenario: Each assurance carries its derived status

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendors` and a
  readable vendor holds a certification
- **THEN** that assurance carries its `standard`, its `expires`, and a `status` the
  endpoint derived from the expiry and the configured warning window, rather than a
  stored or authored one

#### Scenario: Vendor exceptions are served by the unified scopes endpoint

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/scopes` and a readable
  vendor has a scope with a requirement or control target
- **THEN** the response includes that scope with its `subject` (the vendor id), its target,
  `disposition`, and `justification` (present for every readable `Out` scope)

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/vendors`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests the
  vendors endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Owner-excluded caller sees neither the vendor nor its scopes

- **WHEN** an authenticated caller with no grant reaching a vendor's `owner` (or the
  vendor has a missing or dangling `owner`) requests `GET /api/v1/freeboard/vendors` or
  `GET /api/v1/freeboard/scopes`
- **THEN** neither the vendor row nor any of that vendor's vendor-subject scopes or `Out`
  justifications appear in either response, because vendor readability follows the `owner`
  edge and is not global, so the hidden vendor's id and exception rationale do not leak
