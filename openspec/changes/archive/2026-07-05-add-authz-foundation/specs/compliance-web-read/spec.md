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
