## MODIFIED Requirements

### Requirement: Statement of Applicability is a read-only projection

The web app SHALL serve a Statement of Applicability for a standard as a read-only
projection over the organisation tree, computed from the persisted organisations,
scopes, requirements, and requirement-scopes and stored nowhere. For the given
standard the projection SHALL include every organisation node with its resolved
standard disposition and whether that value is `explicit`, `inherited`, or
`Undetermined`. Each node whose standard resolves `In` SHALL additionally report its
per-requirement exclusions: the requirements of that standard whose
`(organisation, requirement)` nearest-ancestor resolution finds a `RequirementScope`,
each with its resolved disposition (`In` or `Out`) and whether that value is
`explicit` or `inherited`. A requirement of the standard that is not listed for a node
follows that node's standard disposition (`In`). A node whose standard resolves `Out`
or `Undetermined` SHALL report no per-requirement exclusions, because requirement
scopes are not applied there. The endpoint SHALL be GET-only and SHALL NOT be blocked
by GitOps read-only mode. Node output SHALL be deterministically ordered by `id`, and
each node's per-requirement list SHALL be ordered by requirement `id`.

The projection SHALL always be computed over the full organisation tree so that
nearest-ancestor inheritance is correct, then filtered to the caller's accessible
set so a node whose disposition is inherited from an ancestor above the accessible
subtree keeps that inherited value. The `/compliance/statement-of-applicability`
view page SHALL render only the nodes in scope for the active organisation
selection, bounded by the accessible set: when an organisation is selected, the
selected node and its descendants intersected with the accessible set; when the
selection is "All Organisations", every accessible node. The scoping SHALL be
applied server-side so out-of-scope nodes are absent from the rendered page. The
page SHALL name the active organisation scope above the projection: the selected
organisation's title when one is selected, or "All Organisations" when none is. The
page SHALL derive its resolved selection from its own reads - its own organisation
list, its accessible set, and the selection cookie it reads itself - and SHALL NOT
take the resolved selection from the shared request-scoped selection resolver, so a
transient failure that degrades only the resolver's own read cannot drop the page's
scope to "All Organisations". The JSON endpoint
`GET /api/v1/freeboard/statement-of-applicability/{standardId}` SHALL likewise
resolve over the full tree and then return only the nodes in the caller's accessible
set, so the endpoint and the page apply the same authorization boundary.

Authentication precedes this read and shares the same backing store as the
compliance store. So the HTTP 503 unreachable-store response describes the case
where the request is authenticated and only the compliance store is unavailable to
it. A full database outage that also fails authentication surfaces first as an
authentication failure (HTTP 401 for the endpoint, a `/login` redirect for the page)
- the request never reaches the projection - not as this 503 response.

#### Scenario: Projection reflects the tree and dispositions

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard with a company marked `In` and a department left unstated
- **THEN** the response lists the company as `In` `explicit` and the department as
  `In` `inherited`, ordered by `id`

#### Scenario: Projection reports per-requirement exclusions on in-scope nodes

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard where a company resolves `In` and marks one requirement `Out` company-wide
- **THEN** the company node lists that requirement with disposition `Out` marked
  `explicit`, and requirements it does not exclude are absent from the list (they
  follow the node's `In` standard disposition), the list ordered by requirement `id`

#### Scenario: Out-of-scope node reports no per-requirement exclusions

- **WHEN** a node resolves `Out` for the standard
- **THEN** the node reports no per-requirement exclusions, because requirement scopes
  are not applied under an out-of-scope standard

#### Scenario: JSON endpoint returns only accessible nodes with inheritance preserved

- **WHEN** a caller whose accessible set is a strict subset of organisations
  requests the Statement of Applicability JSON endpoint for a standard
- **THEN** the response contains only the nodes in the accessible set, each keeping
  a disposition inherited from an ancestor above the accessible subtree

#### Scenario: Projection is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability
- **THEN** the request is served normally and is not rejected with the 409
