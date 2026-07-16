## MODIFIED Requirements

### Requirement: Web read endpoints serve the persisted compliance domain

The web app SHALL expose read-only HTTP endpoints that return the persisted
compliance domain from the store, not the YAML on disk. These endpoints live under
the single `/api/v1/freeboard/` API namespace and require an authenticated user. It
SHALL provide `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/requirements`, `GET /api/v1/freeboard/organisations`, and
`GET /api/v1/freeboard/scopes`. Standards SHALL include their `version`, `authority`,
optional `publisher`, and optional `source_url` metadata (null when unset). Controls SHALL
include their `maps_to` `Requirement` ids, resolved from the `control_requirements` join,
and their `evaluation` rule (null when unset). Requirements SHALL include their owning
`standard` id, `theme`, `statement`, `guidance` (null when unset), and a `citation`
object of `{ label, url }` composed from the stored `citation_label` and
`citation_url`. Organisations SHALL include their `kind` and resolved `parent` id
(null for a root). Scopes SHALL include their `subject` id, exactly one target
(`standard`, `requirement`, or `control` id, whichever is set, the others null),
`disposition`, and `justification` (null when unset), resolved from the store. The single
`/scopes` endpoint replaces the previous separate `/scopes`, `/requirement-scopes`, and
`/vendor-scopes` endpoints, which are removed. The web app SHALL read through the
`IComplianceStore` abstraction; its read-path dependency-injection registration SHALL
register `IComplianceStore` and SHALL NOT register the GitOps import or the migration
runner abstractions.

The org-scoped reads SHALL be narrowed to the caller's accessible organisation set
(as defined by the authorization enforcement capability): `organisations` filtered
by id, and `scopes` filtered by subject readability. A scope is readable when its
`subject` is readable, with a branch per parent-anchored subject family: a
Company/Department subject is readable when it is in the caller's accessible-organisation
set; a Vendor subject is readable when its `owner` (a Company/Department asset) is in that
set; and a Machine (or other parent-anchored asset) subject is readable when its `parent`
ancestry resolves into that set (walking the asset's `parent` chain to the org node it hangs
under, the same ancestry walk the Statement of Applicability uses). A scope whose subject is
missing, dangling, or otherwise unreadable - an `owner` or resolved parent-org outside the
accessible set, or a subject that resolves to no live asset (no asset row, or a retired
discovered asset) - SHALL be omitted from the response, so neither the subject id nor an `Out`
`justification` leaks; this fail-closed rule preserves the prior `/scopes` org-narrowing and
the prior `/vendor-scopes` owner-narrowing and adds the machine parent-ancestry branch in one
predicate, so a Machine-subject scope is visible to a caller who can see its parent org and
hidden from one who cannot. When a returned organisation's `parent` is not in the
caller's accessible set, its `parent` id SHALL be nulled in the response, so the read does
not disclose the existence of an inaccessible ancestor; such a node reads as a root,
consistent with how the selector already treats it. The non-tenant catalog reads
`standards`, `controls`, and `requirements` are shared reference data with no
confidentiality boundary and SHALL NOT be narrowed; they remain authenticated-only.

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

#### Scenario: Scopes endpoint returns the readable unified mapping

- **WHEN** a client requests `GET /api/v1/freeboard/scopes`
- **THEN** the response lists the scopes whose subject is readable with `id`, `title`,
  `subject` id, the one set target (`standard`, `requirement`, or `control` id), the
  others null, `disposition`, and `justification` (null when unset), ordered by `id`

#### Scenario: Removed requirement-scope and vendor-scope endpoints are gone

- **WHEN** a client requests `GET /api/v1/freeboard/requirement-scopes` or
  `GET /api/v1/freeboard/vendor-scopes`
- **THEN** the endpoint does not exist (their data is served by the unified `/scopes`
  endpoint)

#### Scenario: Unreadable subject hides the scope and its justification

- **WHEN** a caller reads `GET /api/v1/freeboard/scopes` and a scope's subject is outside
  the caller's accessible set (an organisation subject not accessible, a vendor subject
  whose `owner` is missing, dangling, or outside the accessible set, or a machine subject
  whose `parent` ancestry resolves to an org node outside the accessible set)
- **THEN** that scope is omitted from the response, so neither its subject id nor its `Out`
  `justification` is disclosed

#### Scenario: Machine-subject scope follows its parent-org accessibility

- **WHEN** a caller reads `GET /api/v1/freeboard/scopes` and a scope's subject is a `Machine`
  asset whose `parent` ancestry resolves to an org node in the caller's accessible set
- **THEN** that scope is returned; and the same scope is omitted for a caller whose accessible
  set does not include that parent-org node, so a Machine-subject scope is visible only through
  its parent-org accessibility

#### Scenario: Read responses are ordered by id

- **WHEN** a client requests any of the read endpoints
- **THEN** the resources are ordered by `id` and each relation id array is ordered
  by id

### Requirement: Vendor read endpoints serve the persisted vendor register

The web app SHALL expose a read-only HTTP endpoint that returns the persisted vendors THE
CALLER MAY READ from the store, under the single `/api/v1/freeboard/` API namespace,
requiring an authenticated user (any logged-in user; no admin role). It SHALL provide
`GET /api/v1/freeboard/vendors`. Vendors are `Asset` rows of `type: Vendor` and SHALL
include their `id` and `title`. A vendor's per-requirement and per-control exceptions are
served by the unified `GET /api/v1/freeboard/scopes` endpoint as scopes whose `subject` is
that vendor (there is no separate `/vendor-scopes` endpoint); those scopes carry the
target, `disposition`, and `justification` (always present for a readable `Out` scope, so
an exception is never silent). The `/vendors` endpoint SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only mode,
and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the store is
unavailable. Responses SHALL be deterministically ordered by `id`.

The `/vendors` endpoint SHALL narrow its rows to the caller's accessible organisation set
through the vendor `owner` edge, and the unified `/scopes` endpoint SHALL narrow
vendor-subject scopes the same way (a vendor scope is readable only when the vendor's
`owner` is in the caller's accessible set). Read-access is fail-closed: a vendor with a
missing or dangling `owner`, or an `owner` outside the caller's accessible set, SHALL have
BOTH its vendor row (on `/vendors`) AND its vendor-subject scopes (on `/scopes`) hidden
from that caller, so neither the vendor id nor its exception rationale leaks even though
the vendor row is suppressed.

#### Scenario: Vendors endpoint returns the readable vendors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendors` and some
  vendors have an `owner` in the caller's accessible-organisation set
- **THEN** the response lists those vendors with their `id` and `title`, ordered by
  `id`, and omits any vendor whose `owner` is missing, dangling, or outside the
  accessible set

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

### Requirement: Read path tolerates an unavailable store

The web app SHALL NOT require MySQL at startup: an unreachable store SHALL NOT crash
the app or block boot. The web app MAY perform a single guarded, best-effort read
just after the server has started, off the boot-blocking path, whose sole effect is
logging integration-connection token warnings; that read SHALL NOT be awaited before
the server begins accepting requests, so a hung or unreachable store can never delay
or gate boot. That read SHALL be non-fatal and SHALL silently skip - never throwing,
and never blocking or gating boot - on any store outage, so the app still boots when
the store is unreachable. Apart from that one guarded warning read, the web app SHALL
NOT auto-connect to MySQL at startup. When the store is unreachable at request time, a read
endpoint SHALL return a clear error response (an RFC 7807 problem body, HTTP 503)
rather than an unhandled exception, and the `GET /api/v1/freeboard/compliance/status`
endpoint's `persisted` summary SHALL degrade to all-null per-kind values rather
than failing the whole status response. The `persisted` object SHALL remain
present with every per-kind key, each set to `null`. The per-kind key set includes one
unified `scopes` key (the previous separate `requirementScopes` and `vendorScopes` keys
are removed with their read endpoints), and `vendors`, `evidenceCollectors`, and
`attestationTemplates` alongside the pre-existing kinds:

```json
{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "vendors": null, "evidenceCollectors": null, "attestationTemplates": null } }
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

The vendor, evidence-collector, and attestation-template read endpoints
(`GET /api/v1/freeboard/vendors`, `GET /api/v1/freeboard/evidence-collectors`, and
`GET /api/v1/freeboard/attestation-templates`) tolerate the unreachable store the
same way as the other resource reads: HTTP 503 with an RFC 7807 problem body, never
an unhandled exception.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "vendors": null, "evidenceCollectors": null, "attestationTemplates": null }`
  rather than the request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`,
  `/api/v1/freeboard/requirements`, `/api/v1/freeboard/organisations`,
  `/api/v1/freeboard/scopes`, `/api/v1/freeboard/vendors`,
  `/api/v1/freeboard/evidence-collectors`, or
  `/api/v1/freeboard/attestation-templates`
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception

#### Scenario: Guarded post-start warning read skips a store outage without failing boot

- **WHEN** the web app starts and the compliance store is unreachable during the
  single guarded, best-effort integration-connection token warning read that runs
  just after the server has started, off the boot-blocking path
- **THEN** the read is skipped silently, it emits no warning, boot is neither blocked
  nor failed, and the app starts

### Requirement: Compliance status endpoint reports persisted counts

The web app SHALL provide `GET /api/v1/freeboard/compliance/status` returning a
summary of how many standards, controls, requirements, organisations, scopes,
vendors, evidence-collectors, and attestation-templates are currently persisted in the
store. This is the general compliance read surface; the persisted counts live here, NOT on
`GET /api/v1/freeboard/gitops/status` (which stays a GitOps concern reporting
read-only mode and repository URL). The summary SHALL be a `persisted` object with
per-kind counts, including one unified `scopes` count (the separate `requirementScopes`
and `vendorScopes` counts are removed with the merged tables):

```json
{ "persisted": { "standards": 3, "controls": 12, "requirements": 35, "organisations": 4, "scopes": 5, "vendors": 5, "evidenceCollectors": 8, "attestationTemplates": 6 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, requirements, organisations, one unified scopes count,
  vendors, evidence-collectors, and attestation-templates

### Requirement: Compliance reads require an authenticated user

Every compliance read endpoint SHALL require an authenticated user: the resource
reads `GET /api/v1/freeboard/standards`, `GET /api/v1/freeboard/controls`,
`GET /api/v1/freeboard/requirements`, `GET /api/v1/freeboard/organisations`,
`GET /api/v1/freeboard/scopes`,
`GET /api/v1/freeboard/statement-of-applicability/{standardId}`, and
`GET /api/v1/freeboard/compliance/status`. Authentication (any logged-in user) is
sufficient; these reads SHALL NOT require the admin role. An anonymous request to
any of these endpoints SHALL return HTTP 401. Authentication is orthogonal to the
GitOps read-only gate: these GET endpoints SHALL still be served to an authenticated
user when the instance is in read-only mode.

#### Scenario: Anonymous read is rejected

- **WHEN** an anonymous client requests any compliance read endpoint (the resource
  reads including `requirements` and the unified `scopes`, the
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
