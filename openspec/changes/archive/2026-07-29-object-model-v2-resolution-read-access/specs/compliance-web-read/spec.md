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
(null for a root); they are the `Company`- and `Department`-typed rows of the one unified
asset read, so `/organisations` keeps its existing response shape while the store returns one
asset set. Scopes SHALL include their `subject` id, exactly one target
(`standard`, `requirement`, or `control` id, whichever is set, the others null),
`disposition`, and `justification` (null when unset), resolved from the store. The single
`/scopes` endpoint replaces the previous separate `/scopes`, `/requirement-scopes`, and
`/vendor-scopes` endpoints, which are removed. The web app SHALL read through the
`IComplianceStore` abstraction; its read-path dependency-injection registration SHALL
register `IComplianceStore` and SHALL NOT register the GitOps import or the migration
runner abstractions.

The org-scoped reads SHALL be narrowed to the caller's ACCESSIBLE ASSET set (as defined by
the authorization enforcement capability: the caller's grant-rooted organisation read-subtree
union, closed over the asset `parent` chain and the vendor `owner` edge, excluding retired
discovered assets). The narrowing SHALL be ONE membership test per row, not a per-subject-type
rule: `organisations` are filtered by whether the organisation's id is in the accessible asset
set, and `scopes` are filtered by whether the scope's `subject` id is in it. That single test
subsumes the three cases it replaces - a `Company`/`Department` subject admitted by its own id,
a `Vendor` subject admitted through its `owner`, and a `Machine` (or other parent-anchored)
subject admitted through its `parent` ancestry - because the accessible asset set is already
the closure of the organisation union over those edges. A scope whose subject is missing,
dangling, retired, or otherwise outside the accessible asset set SHALL be omitted from the
response, so neither the subject id nor an `Out` `justification` leaks; this fail-closed rule
preserves the prior `/scopes` org-narrowing, the prior `/vendor-scopes` owner-narrowing, and
the machine parent-ancestry branch, and it SHALL NOT depend on any subject metadata carried on
the scope row itself. When a returned organisation's `parent` is not itself a returned row of
that listing - because it is outside the caller's accessible asset set, or because it is not an
organisation-typed asset - its `parent` id SHALL be nulled in the response, so `parent` always
names a row of the same listing or null and the read does not disclose the existence of an
inaccessible ancestor; such a node reads as a root, consistent with how the selector already
treats it. The non-tenant catalog reads `standards`, `controls`, and
`requirements` are shared reference data with no confidentiality boundary and SHALL NOT be
narrowed; they remain authenticated-only.

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
- **THEN** the response lists the `Company` and `Department` assets in the caller's
  accessible set with `id`, `title`, `kind`, and resolved `parent` id (null for a
  root), and lists no `Machine` or `Vendor` asset

#### Scenario: Scopes endpoint returns the readable unified mapping

- **WHEN** a client requests `GET /api/v1/freeboard/scopes`
- **THEN** the response lists the scopes whose `subject` id is in the caller's accessible
  asset set with `id`, `title`, `subject` id, the one set target (`standard`, `requirement`,
  or `control` id), the others null, `disposition`, and `justification` (null when unset),
  ordered by `id`, and carries no subject type, source, state, parent, or owner field

#### Scenario: Removed requirement-scope and vendor-scope endpoints are gone

- **WHEN** a client requests `GET /api/v1/freeboard/requirement-scopes` or
  `GET /api/v1/freeboard/vendor-scopes`
- **THEN** the endpoint does not exist (their data is served by the unified `/scopes`
  endpoint)

#### Scenario: Unreadable subject hides the scope and its justification

- **WHEN** a caller reads `GET /api/v1/freeboard/scopes` and a scope's subject is outside
  the caller's accessible asset set (an organisation subject not accessible, a vendor subject
  whose `owner` is missing, dangling, or outside the accessible set, a machine subject whose
  `parent` ancestry resolves outside it, or a retired discovered subject)
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
`GET /api/v1/freeboard/vendors`. Vendors are `Asset` rows of `type: Vendor`, served from the
one unified asset read rather than a separate vendor read, and SHALL include their `id` and
`title` and no other field. A vendor's per-requirement and per-control exceptions are
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
the vendor row is suppressed.

#### Scenario: Vendors endpoint returns the readable vendors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/vendors` and some
  vendors have an `owner` that resolves into the caller's organisation union
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

### Requirement: Collector read endpoint serves the persisted collectors

The web app SHALL expose a read-only HTTP endpoint that returns the persisted
collectors from the store, under the single `/api/v1/freeboard/` API
namespace, requiring an authenticated user (any logged-in user; no admin role). It
SHALL provide `GET /api/v1/freeboard/collectors`. Each collector SHALL
include its `id`, `title`, `control` id, `vendor` id (null when unset OR when the caller
may not read that vendor), `type`, `provider` (null when unset), `frequency`,
`threshold` (null when unset), and `config` (the typed type-specific config object).

The `vendor` id SHALL be emitted only when that vendor id is in the caller's ACCESSIBLE
ASSET set (as defined by the authorization enforcement capability), and SHALL be emitted
as `null` otherwise. The collector ROW SHALL still be returned in both cases: this
endpoint narrows one FIELD, not its row set, because collectors carry no organisation
dimension. Without this rule the vendor `owner` edge would not actually bound vendor
readability - a caller with no grant reaching a vendor's `owner` would still learn the
vendor's id from a collector row - so the field narrowing is what makes global vendor
readability genuinely dropped rather than dropped on `/vendors` alone. A caller who may
not read the vendor SHALL NOT be able to distinguish "this collector has no vendor" from
"this collector's vendor is hidden from you", because both read `null`.

This rule bounds the vendor ASSET id only. A collector's `title` and `config` describe
the integration rather than the vendor asset and SHALL NOT be narrowed by it.

The `config` object SHALL carry a key only for a member the collector actually has, and
SHALL omit every absent member rather than emitting it as `null` or as an empty array. So
the object MAY carry `body` (a `manual` or `training` collector's markdown), `fields` (the
ordered list of form fields, each with `id`, `label`, `type`, and `options` - an array,
empty for a non-`single-choice` field), `pass_mark`, `quiz` (the ordered list of quiz
items, each with `id`, `prompt`, and `options`), and `checks` (an `integration` collector's
ordered tracked checks, each with `source_key`, `name`, and `severity`). It SHALL be an
empty object for a collector whose registered `(type, provider)` config schema accepts no
key, and equally for one that authored none of the keys its schema does accept. This
mirrors the storage rule that an absent config member is omitted rather than stored as
JSON null, so the response object is the stored object with the answer removed and the keys
re-cased.

This omit-absent rule applies INSIDE `config` only. The collector's own nullable top-level
fields - `vendor`, `provider`, and `threshold` - SHALL still be emitted as explicit `null`
when unset, because the top level is a fixed response shape.

The quiz items SHALL NOT include the `answer`: the correct
answer is confidential authoring data redacted from the read model, so it never
appears in the endpoint response even though every authenticated user may read this
endpoint. An integration collector's `checks` ARE exposed: they are non-secret,
git-authored declarations of what the collector tracks, and they are exposed because they
are a `config` key like any other, not special-cased into or out of the response. The
endpoint SHALL NOT expose the collector's `connection`.

This single endpoint replaces the removed `GET /api/v1/freeboard/evidence-collectors` and
`GET /api/v1/freeboard/attestation-templates`. The endpoint SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only
mode, and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the
store is unavailable. Unlike the per-org resource endpoints (`/organisations`
and `/scopes`), which narrow their ROWS to the caller's accessible asset
set, this endpoint SHALL NOT filter its rows:
collectors are org-independent reference data (they carry no `organisation`
dimension), so any authenticated user - including one with zero org access - may read
every collector. Responses SHALL be deterministically ordered by `id`.

#### Scenario: Collectors endpoint returns the persisted collectors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors`
- **THEN** the response lists each collector with its `id`, `title`, `control`,
  `vendor` (null when unset or unreadable), `type`, `provider` (null when unset),
  `frequency`, `threshold` (null when unset), and typed `config`, ordered by `id`

#### Scenario: An unreadable vendor id is nulled but the collector row remains

- **WHEN** an authenticated caller with no grant reaching a vendor's `owner` requests
  `GET /api/v1/freeboard/collectors` and a collector names that vendor
- **THEN** the collector is still listed with all its other fields and its `vendor` reads
  `null`, so the hidden vendor's id does not leak through the collector listing, and a
  caller whose accessible asset set does contain that vendor sees the vendor id on the
  same row

#### Scenario: Manual and training collectors carry their form in config

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` and the
  store holds a `manual` collector with fields and a `training` collector with a pass mark
  and quiz
- **THEN** each returned collector carries its `body`, `fields`, `pass_mark`, and `quiz`
  under `config`, an `integration` collector carries its `checks` under `config`, and a
  collector of a type whose config schema accepts no key carries an empty `config`

#### Scenario: Absent config members are omitted, not emitted as null or empty

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` and the
  store holds a `script` collector, a `training` collector with a pass mark and quiz but no
  body and no fields, and an `integration` collector with checks
- **THEN** the `script` collector's `config` is `{}`, the `training` collector's `config`
  carries `pass_mark` and `quiz` and no `body`, `fields`, or `checks` key, and the
  `integration` collector's `config` carries `checks` and none of the attestation keys

#### Scenario: Quiz answer is not exposed

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` for a
  training collector whose quiz items each carry a correct `answer`
- **THEN** each returned quiz item includes `prompt` and `options` but no `answer` key,
  so the correct answer is not disclosed to the reader

#### Scenario: Integration checks are exposed under config and the connection is not

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` for a
  `type: integration` collector that names a connection and declares checks
- **THEN** the returned collector carries its checks under `config` with each check's
  `source_key`, `name`, and `severity`, and carries no `connection` key

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/collectors`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests the
  collectors endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Zero-grant caller under strict enforcement still reads every collector

- **WHEN** authorization runs in strict enforce mode and an authenticated caller
  holding no organisation grants requests `GET /api/v1/freeboard/collectors`
- **THEN** the endpoint returns every persisted collector, not narrowed to the empty
  accessible asset set that strict enforcement produces for a zero-grant
  caller, because the collector endpoint intentionally skips the ROW narrowing that the
  per-org resource endpoints apply - while every `vendor` id on those rows reads `null`,
  since none of them is in that empty set

#### Scenario: Retired endpoints no longer answer

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/evidence-collectors`
  or `GET /api/v1/freeboard/attestation-templates`
- **THEN** neither route is mapped and neither returns a collector payload
