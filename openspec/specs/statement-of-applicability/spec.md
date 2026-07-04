# statement-of-applicability Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: Scope disposition resolves by nearest-ancestor inheritance

The system SHALL resolve an organisation node's disposition for a standard as
follows: if a Scope exists for that `(organisation, standard)` pair, its
disposition is the resolved value and is `explicit`; otherwise the resolved value
is the parent's resolved value and is `inherited`; if the node has no ancestor with
a Scope for that standard, the resolved value is `Undetermined`. `Undetermined`
SHALL be distinct from an explicit or inherited `Out`.

#### Scenario: Explicit disposition wins

- **WHEN** a node has a Scope of disposition `Out` for a standard
- **THEN** the node resolves to `Out`, marked `explicit`, regardless of its
  ancestors

#### Scenario: Child inherits nearest ancestor

- **WHEN** a company has disposition `In` for a standard and its department has no
  Scope for that standard
- **THEN** the department resolves to `In`, marked `inherited`

#### Scenario: No ancestor disposition is undetermined

- **WHEN** neither a node nor any ancestor has a Scope for a standard
- **THEN** the node resolves to `Undetermined`, distinct from `Out`

### Requirement: Statement of Applicability requires an authenticated user

The Statement of Applicability SHALL require an authenticated user, both on the
`GET /api/v1/freeboard/statement-of-applicability/{standardId}` endpoint and on the
`/compliance/statement-of-applicability` read-only view page. Authentication (any
logged-in user) is sufficient; neither SHALL require the admin role. An anonymous
request to the endpoint SHALL return HTTP 401; an anonymous browser GET to the page
SHALL be redirected to `/login` rather than rendering the view. Authentication is
orthogonal to the GitOps read-only gate: both the endpoint and the page SHALL still
be served to an authenticated user when the instance is in read-only mode, and both
remain GET-only.

#### Scenario: Anonymous request to the endpoint is rejected

- **WHEN** an anonymous client requests
  `GET /api/v1/freeboard/statement-of-applicability/{standardId}`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Anonymous request to the page redirects to login

- **WHEN** an anonymous browser requests `/compliance/statement-of-applicability`
- **THEN** the response redirects to `/login` rather than rendering the view

#### Scenario: Authenticated user is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability endpoint or view page
- **THEN** the request is served normally and is not rejected with the 409
  read-only response

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
nearest-ancestor inheritance is correct. The `/compliance/statement-of-applicability`
view page SHALL then render only the nodes in scope for the active organisation
selection, bounded by the accessible set: when an organisation is selected, the
selected node and its descendants intersected with the accessible set; when the
selection is "All Organisations", every accessible node. Because the projection is
computed over the full tree before the view is scoped, a node whose disposition is
inherited from an ancestor above the selected node SHALL keep that inherited value.
The scoping SHALL be applied server-side so out-of-scope nodes are absent from the
rendered page. The page SHALL name the active organisation scope above the projection:
the selected organisation's title when one is selected, or "All Organisations" when
none is. The page SHALL derive its resolved selection from its own reads - its own
organisation list, its accessible set, and the selection cookie it reads itself - and
SHALL NOT take the resolved selection from the shared request-scoped selection
resolver, so a transient failure that degrades only the resolver's own read cannot
drop the page's scope to "All Organisations". The JSON endpoint
`GET /api/v1/freeboard/statement-of-applicability/{standardId}`
is unaffected and continues to return every node for the standard.

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

#### Scenario: Projection is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability
- **THEN** the request is served normally and is not rejected with the 409
  read-only response

#### Scenario: Unreachable store returns a problem response

- **WHEN** the store is unreachable and an authenticated user requests the Statement
  of Applicability
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception

#### Scenario: Page scopes to the selected organisation subtree

- **WHEN** an organisation is the active selection and an authenticated user opens
  the Statement of Applicability page for a standard
- **THEN** the page renders only that organisation and its descendants and omits
  organisation nodes outside that subtree

#### Scenario: Inherited disposition from an ancestor above the selection is kept

- **WHEN** a company is marked `In` for a standard, its department has no scope, and
  the department is the active selection
- **THEN** the page renders the department as `In` `inherited`, resolved from the
  company that is outside the rendered subtree

#### Scenario: Page keeps its store-unreachable notice under an outage

- **WHEN** the store is unreachable and an authenticated user opens the Statement of
  Applicability page, which reads its standards and its Statement-of-Applicability
  inputs (organisations, scopes, requirements, requirement-scopes) directly from the
  store and derives its entire scope from its own reads - its accessible set from its
  own organisation read and its resolved selection by reading the selection cookie
  itself - consuming the shared server-side selection resolver for nothing
- **THEN** the page renders its store-unreachable notice rather than an empty table,
  driven by its own direct store reads failing, so the outage still surfaces on the
  page and is not mistaken for a healthy result with no organisations

#### Scenario: Inputs load failure after standards still shows the notice

- **WHEN** the standards load succeeds but the Statement-of-Applicability inputs load
  (which carries the organisation list) fails
- **THEN** the page renders its store-unreachable notice rather than a healthy but
  empty table, because it reads its organisation list from its own inputs read and does
  not take the selection resolver's degraded empty list

#### Scenario: All Organisations renders every node

- **WHEN** the active selection is "All Organisations" and an authenticated user
  opens the Statement of Applicability page for a standard
- **THEN** the page renders every accessible organisation node for the standard,
  ordered by `id`

#### Scenario: Page names the active scope

- **WHEN** an authenticated user opens the Statement of Applicability page with an
  organisation selected, and separately with no selection
- **THEN** the page names that organisation's title as the active scope above the
  projection in the first case, and names "All Organisations" in the second

### Requirement: Requirement disposition resolves by nearest-ancestor inheritance under the standard

The system SHALL resolve an organisation node's disposition for a specific
`Requirement` (owned by a standard) in two layers. First it SHALL resolve the node's
disposition for the requirement's standard by the existing standard-level
nearest-ancestor rule. Then:

- If the standard resolves `Undetermined` at the node, the requirement resolves
  `Undetermined` (there is no in-scope standard to exclude a requirement from).
- If the standard resolves `Out` at the node, the requirement resolves `Out`, and
  requirement-scopes SHALL NOT be consulted: the whole standard, and thus every
  requirement, is out of scope.
- If the standard resolves `In` at the node, the system SHALL consult the
  requirement layer by nearest-ancestor inheritance keyed by `(organisation,
  requirement)`: if a `RequirementScope` exists for that node and requirement, its
  disposition is the resolved value and is `explicit`; otherwise the resolved value
  is the nearest ancestor's `RequirementScope` disposition for that requirement and
  is `inherited`; if no ancestor has a `RequirementScope` for that requirement, the
  requirement follows the standard and resolves `In`.

A requirement-level `In` SHALL NOT re-include a requirement whose standard resolves
`Out` or `Undetermined` at the node; the standard-level result dominates. Within an
`In` standard, a child node's explicit `RequirementScope` SHALL override an ancestor's
inherited one, so a department MAY re-include (`In`) a requirement its parent excluded
(`Out`) company-wide.

#### Scenario: Company-wide exclusion is inherited by departments

- **WHEN** a company resolves `In` for a standard and marks a requirement `Out`
  company-wide, and a department under it has no `RequirementScope` for that
  requirement
- **THEN** the department resolves that requirement `Out`, marked `inherited`, while
  requirements it does not exclude follow the standard and resolve `In`

#### Scenario: Department re-includes a company-excluded requirement

- **WHEN** a company marks a requirement `Out` company-wide and a department under it
  marks the same requirement `In`
- **THEN** the department resolves that requirement `In`, marked `explicit`,
  overriding the inherited company `Out`

#### Scenario: Requirement scopes ignored when the standard is out

- **WHEN** a node resolves `Out` for a standard and a `RequirementScope` marks one of
  that standard's requirements `In` at or above the node
- **THEN** the requirement resolves `Out` (following the standard) and the `In`
  requirement-scope is not applied

#### Scenario: Requirement is undetermined when the standard is undetermined

- **WHEN** a node resolves `Undetermined` for a standard
- **THEN** every requirement of that standard resolves `Undetermined` at the node,
  regardless of any `RequirementScope`

#### Scenario: Requirement-scope of another standard is excluded from the projection

- **WHEN** the Statement of Applicability is resolved for one standard while a
  `RequirementScope` binds an organisation to a requirement of a different standard
- **THEN** that requirement-scope does not appear in the requested standard's
  projection, because requirement-scopes are filtered to the requested standard by
  their requirement's owning standard (`Requirement.standard`)

#### Scenario: Child re-including the standard inherits the parent's requirement-scope

- **WHEN** a parent node resolves the standard `Out`, a child under it explicitly
  scopes the standard `In`, and the parent carries a `RequirementScope` marking one of
  the standard's requirements `Out`
- **THEN** the parent reports no per-requirement exclusions (requirement-scopes are not
  applied under its `Out` standard), while the child, whose standard now resolves `In`,
  resolves that requirement `Out` marked `inherited` from the parent's requirement-scope
  (the requirement-layer nearest-ancestor walk ignores the intermediate standard `Out`
  at the parent)

